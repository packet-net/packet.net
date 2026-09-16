using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// When a dial negotiates: §6.3.2 ¶1, "Parameter negotiation occurs only before the
/// connection is made."
/// </summary>
/// <remarks>
/// The spec disagrees with itself about this - Figure D.3 and the green, editorial
/// MDL-NEGOTIATE box on figc4.6's UA arm both put the exchange after the UA, which is what
/// direwolf does, while §6.3.2's own sentence and LinBPQ's responder both put it before the
/// SABM(E) (packethacking/ax25spec#113). Negotiating first is what we do on both moduli: it
/// reaches every peer, the link is never carrying traffic under parameters that are about to
/// change, and a lost XID costs retries on a channel rather than on a live connection. The
/// post-UA exchange stays as the fallback for peers that only answer there.
/// </remarks>
public class PreConnectNegotiationTests
{
    private static readonly Callsign Call1 = new("M0LTE", 1);
    private static readonly Callsign Call2 = new("M0LTE", 2);

    private const int XidBase = 0xAF;
    private static bool IsXidCommand(Ax25Frame f) => (f.Control & 0xEF) == XidBase && f.IsCommand;

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

    private static List<Ax25Frame> Parse(LoopbackModem modem) =>
        modem.SentFrames.SnapshotList()
            .Select(b => Ax25Frame.TryParse(b.Span, Ax25ParseOptions.Lenient, out var f) ? f : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

    [Fact]
    public async Task A_settled_negotiation_is_not_run_again_over_the_live_link()
    {
        // The whole point of negotiating first: exactly one XID exchange, and it is over
        // before the SABME goes out. Seen on air before this, a v2.2 dial negotiated after
        // the UA, so one lost XID became seven commands over nineteen seconds on top of a
        // connection that was already up.
        var callerModem = new LoopbackModem();
        var answererModem = new LoopbackModem();
        await using var caller = new Ax25Listener(callerModem, new Ax25ListenerOptions { MyCall = Call1, K = 8 });
        await using var answerer = new Ax25Listener(answererModem, new Ax25ListenerOptions { MyCall = Call2, K = 8 });

        Ax25Session? inbound = null;
        answerer.SessionAccepted += (_, e) => Volatile.Write(ref inbound, e.Session);
        await caller.StartAsync();
        await answerer.StartAsync();
        answerer.AcceptIncoming = true;
        caller.AcceptIncoming = false;

        using var wire = Wire(callerModem, answererModem);
        var session = await caller.ConnectAsync(Call2, Call1, extended: true).WithTimeout(TimeSpan.FromSeconds(10));
        await WaitFor(() => Volatile.Read(ref inbound) is not null, TimeSpan.FromSeconds(5));

        // Give a stray post-UA exchange every chance to appear before counting.
        await Task.Delay(1000);

        var sent = Parse(callerModem);
        sent.Count(IsXidCommand).Should().Be(1, "the parameters were settled before the connect, so nothing renegotiates them");

        var xidIndex = sent.FindIndex(IsXidCommand);
        var sabmeIndex = sent.FindIndex(f => f.FrameType == Ax25FrameType.Sabme);
        xidIndex.Should().BeLessThan(sabmeIndex, "the XID command precedes the SABME");

        session.Context.ParametersNegotiated.Should().BeTrue();
        session.Context.IsExtended.Should().BeTrue("the negotiation reports the version, it does not choose it");
        Volatile.Read(ref inbound)!.Context.IsExtended.Should().BeTrue();
        session.Context.SrejEnabled.Should().BeTrue("both ends offered selective reject");
    }

    [Fact]
    public async Task A_peer_that_ignores_the_pre_connect_XID_still_negotiates_after_the_UA()
    {
        // direwolf answers XID only once the link is up (figc4.6's UA arm). The fallback
        // has to survive, or negotiating early would cost us the peers that negotiate late.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = Call1,
            T1V = TimeSpan.FromMilliseconds(500),   // keep the pre-connect budget short
        });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(Call2, Call1, extended: true);

        // The XID leads; say nothing, as a peer that only answers post-UA would.
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Parse(modem)[0].Should().Match<Ax25Frame>(f => IsXidCommand(f));

        // The dial gives up waiting and sends the SABME; answer it.
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(10));
        Parse(modem)[1].FrameType.Should().Be(Ax25FrameType.Sabme);
        modem.InjectInbound(Ax25Frame.Ua(Call1, Call2, finalBit: true));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        session.Context.ParametersNegotiated.Should().BeFalse("nobody answered the pre-connect XID");

        // figc4.6's UA arm raises MDL-NEGOTIATE, and with nothing settled it runs.
        await modem.SentFrames.WaitForCountAsync(3, TimeSpan.FromSeconds(5));
        Parse(modem).Skip(2).Should().Contain(f => IsXidCommand(f),
            "an unsettled link still negotiates after the UA - the fallback for peers that answer only there");
    }

    [Fact]
    public async Task What_the_peer_asked_for_survives_the_SABME()
    {
        // The peer answers the pre-connect XID with implicit reject. Establishment must not
        // then hand the link selective reject anyway: §6.3.2 ¶7, both TNCs set up on the
        // values in the XID response.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Call1, K = 8 });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(Call2, Call1, extended: true);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        var peerOffer = new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault,
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = RejectMode.ImplicitReject, Modulo128 = true },
            IFieldLengthRxBits = XidParameters.OctetsToBits(256),
            WindowSizeRx = 8,
        };
        modem.InjectInbound(Ax25Frame.Xid(Call1, Call2, XidInfoField.Encode(peerOffer), isCommand: false, pollFinal: true));

        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(10));
        Parse(modem)[1].FrameType.Should().Be(Ax25FrameType.Sabme, "mod-128 survived: both offered it");
        modem.InjectInbound(Ax25Frame.Ua(Call1, Call2, finalBit: true));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        session.Context.IsExtended.Should().BeTrue();
        session.Context.SrejEnabled.Should().BeFalse("the peer asked for implicit reject and establishment must not override it");
        session.Context.ImplicitReject.Should().BeTrue();

        // And nothing renegotiates it afterwards.
        await Task.Delay(500);
        Parse(modem).Count(IsXidCommand).Should().Be(1);
    }
}
