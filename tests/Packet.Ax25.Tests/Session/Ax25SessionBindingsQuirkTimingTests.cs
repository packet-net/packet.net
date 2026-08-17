using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Ax25Event = Packet.Ax25.Session.Ax25Event;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Quirks are read per dispatch, not captured when the guard bindings are built.
/// <see cref="Ax25SessionContext.Quirks"/> is a settable property and the listener
/// runs its <c>ConfigureSession</c> hook <em>after</em> building the bindings, so a
/// hook selecting <see cref="Ax25SessionQuirks.StrictlyFaithful"/> used to leave the
/// ax25spec#40 and #43 wrappers stuck in whatever state the context had at
/// construction, while every other quirk followed the hook
/// (packet-net/packet.net#696).
/// </summary>
public class Ax25SessionBindingsQuirkTimingTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    [Fact]
    public void Spec43_flow_off_inversion_follows_a_quirk_set_after_the_bindings_were_built()
    {
        var ctx = new Ax25SessionContext { Local = LocalCall, Remote = PeerCall, OwnReceiverBusy = false };
        Ax25Event trigger = new DlFlowOffRequest();
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, new SystemTimerScheduler(new FakeTimeProvider()), () => trigger);

        bindings[Ax25Guard.OwnReceiverBusy]().Should().BeTrue(
            "the default quirk set inverts own_receiver_busy for DL-FLOW-OFF so a not-busy station enters busy");

        ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;

        bindings[Ax25Guard.OwnReceiverBusy]().Should().BeFalse(
            "with the quirk off the figure-literal guard applies, from this dispatch on");
    }

    [Fact]
    public void Spec40_out_of_window_discard_follows_a_quirk_set_after_the_bindings_were_built()
    {
        var ctx = new Ax25SessionContext
        {
            Local = LocalCall,
            Remote = PeerCall,
            K = 4,
            VR = 0,
            RejectException = false,
        };
        // N(S) = 6 with V(R) = 0 and k = 4: outside the receive window [0, 4).
        Ax25Event trigger = new IFrameReceived(Ax25Frame.I(
            destination: LocalCall, source: PeerCall, nr: 0, ns: 6, info: "x"u8));
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, new SystemTimerScheduler(new FakeTimeProvider()), () => trigger);

        bindings[Ax25Guard.RejectException]().Should().BeTrue(
            "the default quirk set ORs the out-of-window condition into reject_exception (the discard path)");

        ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;

        bindings[Ax25Guard.RejectException]().Should().BeFalse(
            "with the quirk off only the figure's own reject_exception flag counts");
    }
}
