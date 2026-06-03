using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Ax25Event = Packet.Ax25.Session.Ax25Event;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for the frame-aware atom bindings in
/// <see cref="Ax25SessionBindings.CreateDefault"/>. Atoms like
/// <see cref="Ax25Guard.PEq1"/>, <see cref="Ax25Guard.Command"/>,
/// <see cref="Ax25Guard.NsEqVr"/>, and <see cref="Ax25Guard.VaLeNrLeVs"/> read
/// from the current trigger event's attached frame rather than
/// constructor-time constants — otherwise figc4.4's I-receive paths can never
/// be evaluated in production.
/// </summary>
public class FrameAwareBindingsTests
{
    private static readonly Callsign Local  = new("M0LTE", 0);
    private static readonly Callsign Remote = new("G7XYZ", 7);

    private static (IReadOnlyDictionary<Ax25Guard, Func<bool>> bindings,
                    Ax25SessionContext ctx,
                    Action<Ax25Event?> setTrigger) NewBindings()
    {
        var ctx = new Ax25SessionContext { Local = Local, Remote = Remote };
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        Ax25Event? trigger = null;
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler, currentTrigger: () => trigger);
        return (bindings, ctx, e => trigger = e);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void P_eq_1_Reads_Incoming_Poll_Bit(bool pollBit)
    {
        var (b, _, setTrigger) = NewBindings();
        var frame = Ax25Frame.Ui(
            destination: Local, source: Remote,
            info: "x"u8, pollFinal: pollBit);
        setTrigger(new UiReceived(frame));

        b[Ax25Guard.PEq1]().Should().Be(pollBit);
        b[Ax25Guard.FEq1]().Should().Be(pollBit, "F_eq_1 is the same wire bit as P_eq_1");
        b[Ax25Guard.POrFEq1]().Should().Be(pollBit);
    }

    [Fact]
    public void P_eq_1_Returns_False_When_No_Trigger_Frame()
    {
        var (b, _, _) = NewBindings();
        b[Ax25Guard.PEq1]().Should().BeFalse("no trigger set");
    }

    [Fact]
    public void P_eq_1_Returns_False_For_Upper_Layer_Trigger_With_No_Frame()
    {
        var (b, _, setTrigger) = NewBindings();
        setTrigger(new DlConnectRequest());
        b[Ax25Guard.PEq1]().Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Command_Reads_Incoming_C_Bits(bool isCommand)
    {
        var (b, _, setTrigger) = NewBindings();
        var frame = Ax25Frame.Sabm(Local, Remote, pollBit: true);
        // Build a response variant by hand if we need isCommand=false. Easier:
        // just use UA for the response case (which has C=0 in dest, C=1 in src).
        if (!isCommand)
        {
            frame = Ax25Frame.Ua(Local, Remote, finalBit: false);
        }
        setTrigger(new SabmReceived(frame));

        b[Ax25Guard.Command]().Should().Be(isCommand);
    }

    [Fact]
    public void N_s_eq_V_r_Reads_Incoming_I_Frame_N_S_Against_Session_V_R()
    {
        var (b, ctx, setTrigger) = NewBindings();
        ctx.VR = 4;

        // I-frame with N(S)=4 — should match.
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 4, info: "x"u8)));
        b[Ax25Guard.NsEqVr]().Should().BeTrue();

        // I-frame with N(S)=5 — shouldn't match.
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 5, info: "x"u8)));
        b[Ax25Guard.NsEqVr]().Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 4, 0, false)]  // window [V(a)=2..V(s)=4], N(R)=0 — mod-8 delta=6 > span=2
    [InlineData(2, 4, 2, true)]   // N(R)=V(a) — fully acked, at lower edge
    [InlineData(2, 4, 3, true)]   // N(R) in the middle
    [InlineData(2, 4, 4, true)]   // N(R)=V(s) — at upper edge
    [InlineData(2, 4, 5, false)]  // N(R) beyond V(s) — invalid
    [InlineData(6, 2, 0, true)]   // wraps: span=4, delta=(0-6+8)%8=2 (in window)
    [InlineData(6, 2, 7, true)]   // wraps: delta=1 (in window)
    [InlineData(6, 2, 3, false)]  // wraps: delta=5 > span=4
    public void Nr_In_Window_Handles_Mod_8_Wrap_Around(byte va, byte vs, byte incomingNr, bool expectedInWindow)
    {
        var (b, ctx, setTrigger) = NewBindings();
        ctx.VA = va;
        ctx.VS = vs;
        // S-frame carries N(R); use RR.
        setTrigger(new RrReceived(Ax25Frame.Rr(Local, Remote, nr: incomingNr, isCommand: false)));

        b[Ax25Guard.VaLeNrLeVs]().Should().Be(expectedInWindow, "va_le_nr_le_vs is the in-window check");
    }

    [Fact]
    public void Info_Field_Valid_Checks_Against_N1()
    {
        var (b, ctx, setTrigger) = NewBindings();
        ctx.N1 = 256;
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 0, info: new byte[256])));
        b[Ax25Guard.InfoFieldLengthLeN1AndContentIsOctetAligned]().Should().BeTrue();

        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 0, info: new byte[257])));
        b[Ax25Guard.InfoFieldLengthLeN1AndContentIsOctetAligned]().Should().BeFalse("exceeds ctx.N1");
    }

    // ─── Non-frame-aware bindings still work ───────────────────────────

    [Fact]
    public void Existing_Flag_Bindings_Still_Work_With_Frame_Aware_Variant()
    {
        var (b, ctx, _) = NewBindings();
        ctx.OwnReceiverBusy = true;
        ctx.Layer3Initiated = false;

        b[Ax25Guard.OwnReceiverBusy]().Should().BeTrue();
        b[Ax25Guard.Layer3Initiated]().Should().BeFalse();
    }

    [Fact]
    public void Binding_Table_Is_Exhaustive_Over_Ax25Guard()
    {
        // The typed table binds every Ax25Guard atom (the whole point of the
        // SP-010 retype — see Ax25SessionBindings.CreateDefault). This holds
        // even with no currentTrigger wired: the frame-aware atoms are present,
        // bound to closures that return safe defaults on a null trigger.
        var ctx = new Ax25SessionContext { Local = Local, Remote = Remote };
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);

        foreach (Ax25Guard atom in Enum.GetValues<Ax25Guard>())
            bindings.Should().ContainKey(atom);

        // Frame-aware atoms with no trigger wired evaluate to safe defaults.
        bindings[Ax25Guard.PEq1]().Should().BeFalse("no trigger → no poll bit");
        bindings[Ax25Guard.Command]().Should().BeFalse("no trigger → not a command");
        bindings[Ax25Guard.NsEqVr]().Should().BeFalse("no trigger → no incoming N(S)");
        bindings[Ax25Guard.VaLeNrLeVs]().Should().BeFalse("no trigger → out of window");
    }
}
