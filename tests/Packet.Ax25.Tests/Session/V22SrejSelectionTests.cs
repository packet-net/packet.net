using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The reject scheme a link ends up with, per modulus.
/// </summary>
/// <remarks>
/// Selective reject is the v2.2 selection: section 6.3.2 paragraph 1426 makes it the
/// default when neither side names one, and figc4.7's <c>Set_Version_2_2</c> body
/// chooses it. It has to be chosen at establishment rather than during negotiation,
/// because the XID exchange that follows a SABME builds its offer from the link context
/// (<c>Ax25ManagementDataLink.DefaultOfferFor</c> reads <c>SrejEnabled</c>): a link that
/// establishes on implicit reject offers implicit reject, and the reverts-to-the-lesser
/// merge can then never arrive at SREJ however capable both ends are. That was
/// packet-net/packet.net#817, where a modulo-8 dial to a peer negotiated SREJ through the
/// pre-SABM XID probe and a modulo-128 dial to the same peer came up go-back-N.
/// <para>
/// The answering side takes the selection from the SABM(E) it receives, where the figc4.1
/// arms carry the <c>Set Version</c> verb. The initiating side's figc4.6 UA arm carries no
/// version verb at all - an outbound link's version is the dial's own choice - so the dial
/// makes the reject-scheme choice with it.
/// </para>
/// </remarks>
public class V22SrejSelectionTests
{
    private static readonly Callsign Call1 = new("M0LTE", 1);
    private static readonly Callsign Call2 = new("M0LTE", 2);

    // Pump each modem's outbound frames into the other's inbound stream.
    private static CancellationTokenSource Wire(LoopbackModem a, LoopbackModem b)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            int ai = 0, bi = 0;
            while (!cts.IsCancellationRequested)
            {
                while (ai < a.SentFrames.Count)
                {
                    b.InjectInboundRaw(a.SentFrames[ai++]);
                }

                while (bi < b.SentFrames.Count)
                {
                    a.InjectInboundRaw(b.SentFrames[bi++]);
                }

                try { await Task.Delay(5, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
        return cts;
    }

    private static async Task<(Ax25Session Caller, Ax25Session Answerer, CancellationTokenSource Wire)>
        ConnectPairAsync(Ax25Listener caller, Ax25Listener answerer, LoopbackModem callerModem, LoopbackModem answererModem, bool extended)
    {
        Ax25Session? inbound = null;
        answerer.SessionAccepted += (_, e) => Volatile.Write(ref inbound, e.Session);

        await caller.StartAsync();
        await answerer.StartAsync();
        answerer.AcceptIncoming = true;
        caller.AcceptIncoming = false;

        var wire = Wire(callerModem, answererModem);
        var session = await caller.ConnectAsync(Call2, Call1, extended).WithTimeout(TimeSpan.FromSeconds(10));
        await WaitFor(() => Volatile.Read(ref inbound) is not null, TimeSpan.FromSeconds(5));
        return (session, Volatile.Read(ref inbound)!, wire);
    }

    [Fact]
    public async Task A_mod128_link_between_two_capable_stations_ends_up_on_SREJ()
    {
        var callerModem = new LoopbackModem();
        var answererModem = new LoopbackModem();
        await using var caller = new Ax25Listener(callerModem, new Ax25ListenerOptions { MyCall = Call1, K = 8 });
        await using var answerer = new Ax25Listener(answererModem, new Ax25ListenerOptions { MyCall = Call2, K = 8 });

        var (session, peer, wire) = await ConnectPairAsync(caller, answerer, callerModem, answererModem, extended: true);
        using var _ = wire;

        await WaitFor(() => session.Context.SrejEnabled && peer.Context.SrejEnabled, TimeSpan.FromSeconds(10),
            "a v2.2 link between two SREJ-capable stations should negotiate selective reject");

        session.Context.IsExtended.Should().BeTrue();
        peer.Context.IsExtended.Should().BeTrue();
        session.Context.ImplicitReject.Should().BeFalse();
        peer.Context.ImplicitReject.Should().BeFalse();

        // The configured window survives establishment; with SREJ in effect the engine
        // holds it to half the modulus (ax25spec#13), which at k=8 changes nothing.
        session.Context.K.Should().Be(8);
        session.Context.EffectiveWindow.Should().Be(8);
        session.Context.N1.Should().Be(256, "establishment does not apply the figc4.7 body's N1 := 2048 either");
    }

    [Fact]
    public async Task A_mod8_dial_without_the_XID_probe_is_still_go_back_N()
    {
        // The modulo-8 leg is untouched: v2.0's selection is implicit reject, and SREJ on
        // modulo 8 comes only from the pre-SABM XID probe, switched off here.
        var callerModem = new LoopbackModem();
        var answererModem = new LoopbackModem();
        await using var caller = new Ax25Listener(callerModem, new Ax25ListenerOptions
        {
            MyCall = Call1,
            PreConnectXidNegotiatesSrej = false,
        });
        await using var answerer = new Ax25Listener(answererModem, new Ax25ListenerOptions { MyCall = Call2 });

        var (session, peer, wire) = await ConnectPairAsync(caller, answerer, callerModem, answererModem, extended: false);
        using var _ = wire;

        await Task.Delay(500);

        session.Context.IsExtended.Should().BeFalse();
        session.Context.SrejEnabled.Should().BeFalse();
        peer.Context.SrejEnabled.Should().BeFalse();
        session.Context.ImplicitReject.Should().BeTrue();
        peer.Context.ImplicitReject.Should().BeTrue();
    }

    [Fact]
    public async Task A_v20_peer_that_FRMRs_the_SABME_takes_the_link_back_to_go_back_N()
    {
        // LinBPQ's answer to a SABME it cannot do. The fallback drops the link to modulo 8,
        // and the v2.2 selective-reject selection has to go with it: a v2.0 link nobody
        // negotiated SREJ on must not be left sending SREJs.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = Call1,
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(Call2, Call1, extended: true);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // FRMR the SABME, wait for the SABM retry, then accept it.
        modem.InjectInbound(Ax25Frame.Frmr(Call1, Call2, [0, 0, 0]));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
        modem.InjectInbound(Ax25Frame.Ua(Call1, Call2, finalBit: true));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        session.Context.IsExtended.Should().BeFalse("the FRMR dropped the dial back to v2.0");
        session.Context.SrejEnabled.Should().BeFalse("a v2.0 link selects implicit reject");
        session.Context.ImplicitReject.Should().BeTrue();
    }
}
