using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Two stations dial each other at the same moment and one UA of the crossing is lost. Each
/// answers the other's SABM(E) with UA while it waits for its own (figc4.2 / figc4.6), so A,
/// whose UA to B went missing, still connects on B's UA to it, while B keeps waiting and on T1
/// sends its SABM(E) again. A is connected by then and may already have sent data; figc4.4 reads
/// the SABM(E) as the §6.5 reset and, with that data outstanding, discards the I-frame queue.
/// B, which threw A's I frames away while it waited (§6.3.1), never gets them. Seen by DAPPS on
/// pdn 0.57.0: with <c>crossed:true</c> it sends at once, and about one round in nine lost its
/// <c>exchange</c> this way.
/// </summary>
public sealed class Ax25ListenerRepeatedConnectSabmTests
{
    private static readonly Callsign CallA = new("N0AAA", 3);
    private static readonly Callsign CallB = new("N0BBB", 3);
    private static readonly TimeSpan T1V = TimeSpan.FromMilliseconds(400);

    // Two modems joined by a polling pump. Both directions can be held (so two dials really do
    // cross: each SABM(E) is on the air before the other is heard), and a predicate can lose
    // one of A's frames on the way to B.
    private sealed class CrossingWire : IDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private volatile bool hold = true;
        private int dropped;

        public CrossingWire(LoopbackModem a, LoopbackModem b, Func<Ax25Frame, bool> loseFromAOnce)
        {
            _ = Task.Run(async () =>
            {
                int ai = 0, bi = 0;
                while (!cts.IsCancellationRequested)
                {
                    if (!hold)
                    {
                        while (ai < a.SentFrames.Count)
                        {
                            var bytes = a.SentFrames[ai++];
                            if (Volatile.Read(ref dropped) == 0
                                && Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, out var f)
                                && loseFromAOnce(f))
                            {
                                Interlocked.Exchange(ref dropped, 1);
                                continue;
                            }

                            b.InjectInboundRaw(bytes);
                        }

                        while (bi < b.SentFrames.Count)
                        {
                            a.InjectInboundRaw(b.SentFrames[bi++]);
                        }
                    }

                    try { await Task.Delay(5, cts.Token); } catch (OperationCanceledException) { return; }
                }
            });
        }

        public bool Dropped => Volatile.Read(ref dropped) != 0;

        public void Release() => hold = false;

        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static Ax25Listener Station(LoopbackModem modem, Callsign me, Ax25SessionQuirks quirks) => new(modem, new Ax25ListenerOptions
    {
        MyCall = me,
        T1V = T1V,
        Quirks = quirks,
    });

    private static List<Ax25Frame> Sent(LoopbackModem modem) =>
        modem.SentFrames.SnapshotList()
            .Select(b => Ax25Frame.TryParse(b.Span, Ax25ParseOptions.Lenient, out var f) ? f : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

    private static bool IsEstablish(Ax25Frame f) => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme;

    // Both dial, A's UA to B is lost, A connects and sends at once. Returns both links, A's link
    // signals from the moment it connected, and what B's application received.
    private static async Task<(Ax25Session onA, Ax25Session onB, List<DataLinkSignal> signalsOnA, Func<string> receivedOnB, LoopbackModem modemA, LoopbackModem modemB, IAsyncDisposable cleanup)> CrossWithALostUaAsync(
        Ax25SessionQuirks quirks, bool extended)
    {
        var modemA = new LoopbackModem();
        var modemB = new LoopbackModem();
        var a = Station(modemA, CallA, quirks);
        var b = Station(modemB, CallB, quirks);
        await a.StartAsync();
        await b.StartAsync();
        var wire = new CrossingWire(modemA, modemB, f => f.FrameType == Ax25FrameType.Ua);

        var dialA = a.ConnectAsync(CallB, CallA, extended, preConnectXidNegotiatesSrej: false);
        var dialB = b.ConnectAsync(CallA, CallB, extended, preConnectXidNegotiatesSrej: false);
        await WaitFor(() => Sent(modemA).Any(IsEstablish) && Sent(modemB).Any(IsEstablish),
            TimeSpan.FromSeconds(5), "both SABM(E)s are on the air before either is heard");
        wire.Release();

        var onA = await dialA.WithTimeout(TimeSpan.FromSeconds(10));
        var signalsOnA = new List<DataLinkSignal>();
        onA.DataLinkSignalEmitted += (_, s) => { lock (signalsOnA) { signalsOnA.Add(s); } };
        wire.Dropped.Should().BeTrue("A's UA answering B's SABM(E) was lost");

        // What DAPPS does on crossed:true: speak at once.
        a.SendData(onA, "exchange\r"u8.ToArray());

        var onB = await dialB.WithTimeout(TimeSpan.FromSeconds(10));
        var received = new System.Text.StringBuilder();
        onB.AttachConsumerWithReplay((_, s) =>
        {
            if (s is DataLinkDataIndication di)
            {
                lock (received) { received.Append(System.Text.Encoding.ASCII.GetString(di.Info.Span)); }
            }
        });
        string ReceivedOnB() { lock (received) { return received.ToString(); } }

        return (onA, onB, signalsOnA, ReceivedOnB, modemA, modemB, new Cleanup(wire, a, b));
    }

    private sealed class Cleanup(CrossingWire wire, Ax25Listener a, Ax25Listener b) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            wire.Dispose();
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_of_the_crossing_SABM_is_answered_again_and_the_data_sent_meanwhile_arrives(bool extended)
    {
        var (onA, onB, signalsOnA, receivedOnB, modemA, _, cleanup) = await CrossWithALostUaAsync(Ax25SessionQuirks.Default, extended);
        await using var _ = cleanup;

        await WaitFor(() => receivedOnB().Contains("exchange", StringComparison.Ordinal), TimeSpan.FromSeconds(10),
            "A's data reaches B's application, retransmitted once B is connected");

        onA.CurrentState.Should().BeOneOf("Connected", "TimerRecovery");
        onB.CurrentState.Should().BeOneOf("Connected", "TimerRecovery");
        Sent(modemA).Count(IsEstablish).Should().Be(1, "A never re-established");
        Sent(modemA).Count(f => f.FrameType == Ax25FrameType.Ua).Should().Be(2, "A answered B's SABM(E) and then its repeat");
        lock (signalsOnA)
        {
            signalsOnA.Where(s => s is DataLinkConnectIndication or DataLinkErrorIndication).Should().BeEmpty(
                "the repeat did not reset A's link");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_the_quirk_off_the_repeated_SABM_resets_as_the_figure_draws_and_the_data_is_lost(bool extended)
    {
        // The paired test: figc4.4's SABM(E) arm with V(s) != V(a) discards the I-frame queue,
        // so the data A sent while B was still waiting is gone. Only this quirk is off: under
        // StrictlyFaithful a mod-128 dial does not route through AwaitingV22Connection
        // (Ax25Spec44), so the crossing itself goes differently.
        var (onA, _, signalsOnA, receivedOnB, _, _, cleanup) = await CrossWithALostUaAsync(
            Ax25SessionQuirks.Default with { RepeatedConnectSabmReacknowledged = false }, extended);
        await using var _ = cleanup;

        await WaitFor(() => { lock (signalsOnA) { return signalsOnA.Any(s => s is DataLinkConnectIndication); } },
            TimeSpan.FromSeconds(10), "the repeated SABM(E) resets A's link (DL-CONNECT indication)");

        // Long enough for A's T1 to have run out several times over had the frame still been queued.
        await Task.Delay(T1V * 6);
        receivedOnB().Should().BeEmpty("the reset discarded the I frame B never saw");
        onA.Context.VS.Should().Be((byte)0);
    }

    [Fact]
    public async Task Strictly_faithful_resets_on_the_repeated_SABM_as_the_figure_draws()
    {
        // StrictlyFaithful clears the quirk with every other; on a mod-8 dial the crossing runs as
        // it does by default (the Ax25Spec44 routing only matters to a mod-128 dial), so this is
        // the figure's reset on its own.
        var (onA, _, signalsOnA, receivedOnB, _, _, cleanup) = await CrossWithALostUaAsync(Ax25SessionQuirks.StrictlyFaithful, extended: false);
        await using var _ = cleanup;

        await WaitFor(() => { lock (signalsOnA) { return signalsOnA.Any(s => s is DataLinkConnectIndication); } },
            TimeSpan.FromSeconds(10), "the repeated SABM resets A's link");
        await Task.Delay(T1V * 6);
        receivedOnB().Should().BeEmpty("the reset discarded the I frame B never saw");
        onA.Context.VS.Should().Be((byte)0);
    }

    [Fact]
    public async Task A_repeat_that_arrives_in_Timer_Recovery_is_answered_again_without_a_reset()
    {
        // Our banner's T1 has run out before the peer's retry arrives, so the link is in Timer
        // Recovery (figc4.5 has the same SABM(E) reset arms as figc4.4).
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();
        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Volatile.Read(ref accepted) is not null, TimeSpan.FromSeconds(5), "the call is accepted");
        var session = Volatile.Read(ref accepted)!;
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, s) => { lock (signals) { signals.Add(s); } };
        listener.SendData(session, "banner\r"u8.ToArray());
        await WaitFor(() => session.CurrentState == "TimerRecovery", TimeSpan.FromSeconds(10),
            "the banner's T1 runs out with nothing from the peer");

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Sent(modem).Count(f => f.FrameType == Ax25FrameType.Ua) == 2, TimeSpan.FromSeconds(5),
            "the repeat is answered with UA");

        session.CurrentState.Should().Be("TimerRecovery", "the link carries on recovering the banner");
        session.Context.VS.Should().Be((byte)1, "the banner is still outstanding");
        lock (signals)
        {
            signals.Where(s => s is DataLinkConnectIndication or DataLinkErrorIndication).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task With_RepeatedConnectUaIgnored_a_doubled_connecting_UA_and_then_the_SABM_repeat_are_both_absorbed()
    {
        // #842 and #856 together, on a path that delivers every frame twice (LinBPQ with two
        // MAP lines): the crossing dial connects on the peer's UA and gets a copy of it, then
        // the peer's SABME retry and a copy of that. Neither quirk's window closes the other's.
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();

        var dial = listener.ConnectAsync(CallB, CallA, extended: true, preConnectXidNegotiatesSrej: false);
        await WaitFor(() => Sent(modem).Any(IsEstablish), TimeSpan.FromSeconds(5), "our SABME is on the air");
        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Ua), TimeSpan.FromSeconds(5),
            "the crossing SABME is answered while we wait");
        var ua = Ax25Frame.Ua(CallA, CallB, finalBit: true);
        modem.InjectInbound(ua);
        modem.InjectInbound(ua);
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, s) => { lock (signals) { signals.Add(s); } };
        listener.SendData(session, "exchange\r"u8.ToArray());

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        modem.InjectInbound(ua);
        await WaitFor(() => Sent(modem).Count(f => f.FrameType == Ax25FrameType.Ua) == 3, TimeSpan.FromSeconds(5),
            "both copies of the retry are answered");

        Sent(modem).Count(IsEstablish).Should().Be(1, "nothing made us re-establish");
        session.Context.VS.Should().Be((byte)1, "the data is still outstanding");
        lock (signals)
        {
            signals.Where(s => s is DataLinkConnectIndication or DataLinkErrorIndication).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_link_the_peer_brought_up_during_our_XID_probe_absorbs_the_peer_s_SABM_retry()
    {
        // #854 and #856 together: the peer's SABME reached us in the probe, figc4.1 connected the
        // link and our UA was lost, so the peer tries again after the dial has returned the link.
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();

        var dial = listener.ConnectAsync(CallB, CallA, extended: true, preConnectXidNegotiatesSrej: true);
        await WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Xid), TimeSpan.FromSeconds(5), "the probe is on the air");
        var xid = Sent(modem).First(f => f.FrameType == Ax25FrameType.Xid);
        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        modem.InjectInbound(Ax25Frame.Xid(CallA, CallB, xid.Info.Span, isCommand: false, pollFinal: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        Sent(modem).Count(IsEstablish).Should().Be(0, "the dial returned the link the peer's call brought up (#854)");
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, s) => { lock (signals) { signals.Add(s); } };
        listener.SendData(session, "exchange\r"u8.ToArray());

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Sent(modem).Count(f => f.FrameType == Ax25FrameType.Ua) == 2, TimeSpan.FromSeconds(5),
            "the retry is answered");

        session.Context.VS.Should().Be((byte)1, "the data is still outstanding");
        lock (signals)
        {
            signals.Where(s => s is DataLinkConnectIndication or DataLinkErrorIndication).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_SABM_after_the_peer_has_sent_anything_else_still_resets_the_link()
    {
        // Narrowness: once the peer has shown it is past the connect (here an RR), a SABM(E) from
        // it is the figure's reset, whatever it looks like: the peer may really have reset.
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();
        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Volatile.Read(ref accepted) is not null, TimeSpan.FromSeconds(5), "the call is accepted");
        var session = Volatile.Read(ref accepted)!;
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, s) => { lock (signals) { signals.Add(s); } };

        listener.SendData(session, "banner\r"u8.ToArray());
        modem.InjectInbound(Ax25Frame.Rr(CallA, CallB, nr: 0, isCommand: false, extended: true));
        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));

        await WaitFor(() => { lock (signals) { return signals.Any(s => s is DataLinkConnectIndication); } },
            TimeSpan.FromSeconds(5), "the SABME after the peer's RR resets the link");
    }

    [Fact]
    public async Task A_different_SABM_still_resets_the_link()
    {
        // Byte for byte: a SABM(E) that differs from the one we answered (here P=0) is not a repeat.
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();
        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB, pollBit: true));
        await WaitFor(() => Volatile.Read(ref accepted) is not null, TimeSpan.FromSeconds(5), "the call is accepted");
        var session = Volatile.Read(ref accepted)!;
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, s) => { lock (signals) { signals.Add(s); } };

        listener.SendData(session, "banner\r"u8.ToArray());
        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB, pollBit: false));

        await WaitFor(() => { lock (signals) { return signals.Any(s => s is DataLinkConnectIndication); } },
            TimeSpan.FromSeconds(5), "a different SABME resets the link");
    }

    [Fact]
    public async Task An_answered_call_whose_UA_was_lost_keeps_the_banner_sent_on_it()
    {
        // The plain inbound case, no crossing: we accept the peer's SABME and send a banner at
        // once, as a node or DAPPS does; our UA is lost, so the peer tries again. The repeat is
        // answered and the banner still reaches the peer (LinBPQ's case 4: "REPEAT OF ORIGINAL
        // SABM COS OTHER END MISSED UA").
        var modem = new LoopbackModem();
        await using var listener = Station(modem, CallA, Ax25SessionQuirks.Default);
        await listener.StartAsync();
        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Volatile.Read(ref accepted) is not null, TimeSpan.FromSeconds(5), "the call is accepted");
        var session = Volatile.Read(ref accepted)!;
        listener.SendData(session, "banner\r"u8.ToArray());

        modem.InjectInbound(Ax25Frame.Sabme(CallA, CallB));
        await WaitFor(() => Sent(modem).Count(f => f.FrameType == Ax25FrameType.Ua) == 2, TimeSpan.FromSeconds(5),
            "the repeat is answered with UA");

        session.Context.VS.Should().Be((byte)1, "the banner is still outstanding, not discarded by a reset");
        session.CurrentState.Should().BeOneOf("Connected", "TimerRecovery");
    }
}
