using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for the frame-aware predicate bindings in
/// <see cref="Ax25SessionBindings.CreateDefault"/>. Predicates like
/// <c>P_eq_1</c>, <c>command</c>, <c>N_s_eq_V_r</c>, and
/// <c>nr_in_window</c> need to read from the current trigger event's
/// attached frame rather than constructor-time constants — otherwise
/// figc4.4's I-receive paths can never be evaluated in production.
/// </summary>
public class FrameAwareBindingsTests
{
    private static readonly Callsign Local  = new("M0LTE", 0);
    private static readonly Callsign Remote = new("G7XYZ", 7);

    private static (IReadOnlyDictionary<string, Func<bool>> bindings,
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

        b["P_eq_1"]().Should().Be(pollBit);
        b["F_eq_1"]().Should().Be(pollBit, "F_eq_1 is the same wire bit as P_eq_1");
        b["P_or_F_eq_1"]().Should().Be(pollBit);
    }

    [Fact]
    public void P_eq_1_Returns_False_When_No_Trigger_Frame()
    {
        var (b, _, _) = NewBindings();
        b["P_eq_1"]().Should().BeFalse("no trigger set");
    }

    [Fact]
    public void P_eq_1_Returns_False_For_Upper_Layer_Trigger_With_No_Frame()
    {
        var (b, _, setTrigger) = NewBindings();
        setTrigger(new DlConnectRequest());
        b["P_eq_1"]().Should().BeFalse();
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

        b["command"]().Should().Be(isCommand);
    }

    [Fact]
    public void N_s_eq_V_r_Reads_Incoming_I_Frame_N_S_Against_Session_V_R()
    {
        var (b, ctx, setTrigger) = NewBindings();
        ctx.VR = 4;

        // I-frame with N(S)=4 — should match.
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 4, info: "x"u8)));
        b["N_s_eq_V_r"]().Should().BeTrue();

        // I-frame with N(S)=5 — shouldn't match.
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 5, info: "x"u8)));
        b["N_s_eq_V_r"]().Should().BeFalse();
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

        b["nr_in_window"]().Should().Be(expectedInWindow);
        b["V_a_le_N_r_le_V_s"]().Should().Be(expectedInWindow, "both predicates are the same check");
    }

    [Fact]
    public void Info_Field_Valid_Checks_Against_N1()
    {
        var (b, ctx, setTrigger) = NewBindings();
        ctx.N1 = 256;
        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 0, info: new byte[256])));
        b["info_field_valid"]().Should().BeTrue();

        setTrigger(new IFrameReceived(Ax25Frame.I(Local, Remote, nr: 0, ns: 0, info: new byte[257])));
        b["info_field_valid"]().Should().BeFalse("exceeds ctx.N1");
    }

    // ─── Non-frame-aware bindings still work ───────────────────────────

    [Fact]
    public void Existing_Flag_Bindings_Still_Work_With_Frame_Aware_Variant()
    {
        var (b, ctx, _) = NewBindings();
        ctx.OwnReceiverBusy = true;
        ctx.Layer3Initiated = false;

        b["own_receiver_busy"]().Should().BeTrue();
        b["layer_3_initiated"]().Should().BeFalse();
    }

    [Fact]
    public void Frame_Aware_Bindings_Absent_When_CurrentTrigger_Not_Wired()
    {
        var ctx = new Ax25SessionContext { Local = Local, Remote = Remote };
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        // Default — no currentTrigger thunk.
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);

        bindings.Should().NotContainKey("P_eq_1");
        bindings.Should().NotContainKey("command");
        bindings.Should().NotContainKey("N_s_eq_V_r");
        // Existing bindings still present:
        bindings.Should().ContainKey("own_receiver_busy");
    }
}
