using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Baseline unit tests for <see cref="Ax25Listener"/> — the first-class
/// inbound session acceptor. Each test wires a <see cref="LoopbackModem"/>
/// in place of a real KISS modem so the test owns both ends of the wire.
/// Inbound SABM/UA/DISC sequences are injected by writing the bytes the
/// peer would send; the listener parses, classifies, and dispatches them,
/// and the test observes the listener's events + the modem's outbound
/// queue.
/// </summary>
/// <remarks>
/// These five tests are the "happy-path" smoke tests that landed alongside
/// the listener itself. Concurrency, multi-peer, cache lifecycle, reject
/// path, spec edge-cases, and hostile event-handler coverage live in
/// sibling files (<c>Ax25ListenerConcurrencyTests</c>,
/// <c>Ax25ListenerMultiPeerTests</c>, <c>Ax25ListenerRejectAndEdgeTests</c>).
/// </remarks>
public class Ax25ListenerTests
{
    private static readonly Callsign LocalCall  = new("M0LTE", 0);
    private static readonly Callsign PeerCallA  = new("G7XYZ", 7);
    private static readonly Callsign PeerCallB  = new("M5ABC", 3);

    [Fact]
    public async Task Listener_Accepts_Inbound_SABM_And_Fires_SessionAccepted()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        Ax25Session? observed = null;
        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            observed = e.Session;
            accepted.TrySetResult(e.Session);
        };

        await listener.StartAsync();

        // Inject inbound SABM from peer A → MYCALL.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.Should().NotBeNull();
        session.Context.Local.Should().Be(LocalCall);
        session.Context.Remote.Should().Be(PeerCallA);

        // The listener should have caused the SDL's t14 (Disconnected →
        // Connected via UA) to run. The modem should have a UA on the
        // outbound queue.
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        modem.SentFrames.Count.Should().BeGreaterThanOrEqualTo(1);
        var ua = modem.SentFrames[0];
        Ax25Frame.TryParse(ua.Span, out var uaFrame).Should().BeTrue();
        // UA is a U-frame with control 0x63 + optional F bit.
        (uaFrame!.Control & 0xEF).Should().Be(0x63, "first emitted frame must be a UA response to the SABM");
        session.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Listener_Reuses_Session_Across_Sequential_Disconnects()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var firstAccepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAccepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        listener.SessionAccepted += (_, e) =>
        {
            int count = Interlocked.Increment(ref acceptedCount);
            if (count == 1) firstAccepted.TrySetResult(e.Session);
            else            secondAccepted.TrySetResult(e.Session);
        };

        await listener.StartAsync();

        // First connect.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var first = await firstAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        first.CurrentState.Should().Be("Connected");

        // Peer disconnects: inject DISC + expect listener's UA, then
        // re-SABM. The cached session should be re-used.
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, PeerCallA));
        await ListenerTestSupport.WaitFor(() => first.CurrentState == "Disconnected", TimeSpan.FromSeconds(2));

        // Mark a context field that's preserved across disconnect so we
        // can spot the reused session. (T1V smoothing isn't easy to
        // assert; the simplest invariant is "same Ax25Session instance".)
        first.Context.T1V = TimeSpan.FromSeconds(7);

        // Second connect from the same peer.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var second = await secondAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        second.Should().BeSameAs(first,
            "the listener's per-peer cache must hand back the same Ax25Session on a second connect from the same peer — preserves SRT/T1V history");
        // Listener-built sessions reset most context state via SDL
        // transitions but T1V is recomputed dynamically by Select_T1_Value
        // — its starting value before the SDL runs should still be the
        // value we set above (the cache didn't blow it away).
        // However the SDL's "T1V := 2 * SRT" on (re)connection resets it,
        // so we don't assert T1V here; the session-instance reuse is the
        // primary observable.
    }

    [Fact]
    public async Task Listener_Drops_DM_For_Disallowed_Inbound()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        })
        {
            AcceptIncoming = false,
        };

        int sessionAcceptedFires = 0;
        listener.SessionAccepted += (_, _) => Interlocked.Increment(ref sessionAcceptedFires);

        await listener.StartAsync();

        // Inbound SABM from a peer; listener should emit DM (figc4.1 t15).
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        modem.SentFrames.Count.Should().Be(1);

        var reply = modem.SentFrames[0];
        Ax25Frame.TryParse(reply.Span, out var replyFrame).Should().BeTrue();
        (replyFrame!.Control & 0xEF).Should().Be(0x0F, "the rejection path must reply DM, not UA");

        // Wait briefly to confirm SessionAccepted does NOT fire.
        await Task.Delay(150);
        sessionAcceptedFires.Should().Be(0, "rejected attempts must not produce a SessionAccepted event");
    }

    [Fact]
    public async Task Listener_Two_Concurrent_Peers()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var sessionsByPeer = new ConcurrentDictionary<Callsign, Ax25Session>();
        var bothAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            sessionsByPeer[e.Session.Context.Remote] = e.Session;
            if (sessionsByPeer.Count == 2) bothAccepted.TrySetResult(true);
        };

        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallB));

        await bothAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));

        sessionsByPeer.Should().HaveCount(2);
        sessionsByPeer[PeerCallA].Context.Remote.Should().Be(PeerCallA);
        sessionsByPeer[PeerCallB].Context.Remote.Should().Be(PeerCallB);
        sessionsByPeer[PeerCallA].Should().NotBeSameAs(sessionsByPeer[PeerCallB],
            "distinct peers must get distinct sessions");
        sessionsByPeer[PeerCallA].CurrentState.Should().Be("Connected");
        sessionsByPeer[PeerCallB].CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Listener_FrameTraced_Fires_For_All_TX_And_RX()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var traced = new List<(FrameDirection Dir, Ax25Frame Frame)>();
        var gate = new object();
        listener.FrameTraced += (_, e) =>
        {
            lock (gate) traced.Add((e.Direction, e.Frame));
        };

        await listener.StartAsync();

        // SABM in → UA out → DISC in → UA out. Four frames total.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, PeerCallA));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));

        // Brief settle so the second TX-trace lands.
        await ListenerTestSupport.WaitFor(() =>
        {
            lock (gate) return traced.Count >= 4;
        }, TimeSpan.FromSeconds(2));

        lock (gate)
        {
            traced.Count.Should().BeGreaterThanOrEqualTo(4);
            traced.Count(t => t.Dir == FrameDirection.Received).Should().BeGreaterThanOrEqualTo(2);
            traced.Count(t => t.Dir == FrameDirection.Transmitted).Should().BeGreaterThanOrEqualTo(2);
        }
    }

    // ─── T1V wiring (m0lte/packet.net#292) ──────────────────────────────
    //
    // Regression cover for the per-port `ax25.t1Ms` lever that silently did
    // nothing on the live node: a configured T1V was seeded onto the session
    // context but the figc4.1 SABM-accept transition runs
    // `SRT := Initial Default; T1V := 2 * SRT` unconditionally, resetting T1V to
    // the spec default (6 s) on every connect. The fix threads the option through
    // the dispatcher's InitialSrt so `T1V := 2 * SRT` reproduces the configured
    // value. These two tests pin both halves: the field survives the handshake,
    // and the live T1 timer actually fires at the configured cadence on the wire.

    [Fact]
    public async Task Configured_T1V_Survives_The_Inbound_Accept_Handshake()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            T1V = TimeSpan.FromMilliseconds(10000),
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));

        // Before the fix this asserted 6000 ms — the SABM-accept path's
        // `SRT := Initial Default (3000); T1V := 2 * SRT` clobbered the seed.
        session.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(10000),
            "the configured T1V must survive the figc4.1 SABM-accept establishment path");
        session.Context.Srt.Should().Be(TimeSpan.FromMilliseconds(5000),
            "SRT is seeded to T1V/2 so `T1V := 2 * SRT` reproduces the configured T1V");
    }

    [Fact]
    public async Task Configured_T1V_Drives_The_T1_Poll_Cadence_On_The_Wire()
    {
        // A node configured with a 10 s T1 must NOT poll at the spec-default
        // ~6 s — that is the live-observed bug: setting ax25.t1Ms did not change
        // the poll cadence. Drive an accepted session, send one I-frame the
        // (loopback) peer never acknowledges, and show T1 expires on the
        // configured 10 s schedule: quiet at 6 s, polling once past 10 s.
        var time = new FakeTimeProvider();
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            T1V = TimeSpan.FromMilliseconds(10000),
        }, time);

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2)); // UA
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));

        // Send an I-frame; the node arms T1 (at the configured T1V) awaiting an
        // ack the loopback peer never sends.
        listener.SendData(session, new byte[] { 0x41 });
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2)); // the I-frame
        var afterIFrame = modem.SentFrames.Count;

        // 6 s in (past the SPEC default but short of the configured 10 s): a node
        // that ignored the config would poll here. Ours must stay silent.
        time.Advance(TimeSpan.FromMilliseconds(6500));
        await Task.Delay(50);   // let any (erroneous) timer callback land
        modem.SentFrames.Count.Should().Be(afterIFrame,
            "with T1V=10s the node must not poll at the 6 s spec default — that was the live bug");

        // Past 10 s total: T1 fires → the node retransmits / polls.
        time.Advance(TimeSpan.FromMilliseconds(4000));
        await modem.SentFrames.WaitForCountAsync(afterIFrame + 1, TimeSpan.FromSeconds(2));
        modem.SentFrames.Count.Should().BeGreaterThan(afterIFrame,
            "T1 must fire on the configured 10 s schedule and trigger a retransmit/poll");
    }

    // ─── N2 wiring (the #292-class clobber for the retry count) ─────────────
    //
    // The figc4.2 outbound-connect establishment path runs `N2 := 10`
    // unconditionally — the same defect class as the T1V clobber above — so a
    // configured N2 was reset to the spec default the moment a connect ran. That
    // made the listener's `(N2+1)·T1V` ConnectAsync backstop always the 66 s spec
    // maximum (the dominant symptom of the #47 node-test flake). The fix seeds the
    // dispatcher's InitialN2 from the configured N2 so `N2 := 10` reproduces it.

    [Fact]
    public async Task Configured_N2_Survives_The_Outbound_Connect_Handshake()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 4,
        });
        await listener.StartAsync();

        // Kick an outbound connect; the listener emits SABM. Reply with the peer's
        // UA so the connect completes, then inspect the established session's N2.
        var connectTask = listener.ConnectAsync(PeerCallA);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2)); // SABM
        modem.InjectInbound(Ax25Frame.Ua(LocalCall, PeerCallA, finalBit: true));

        var session = await connectTask.WithTimeout(TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
        // Before the fix this was 10 — figc4.2's `N2 := 10` clobbered the configured 4.
        session.Context.N2.Should().Be(4,
            "the configured N2 must survive the figc4.2 outbound-connect establishment path");
    }

    [Fact]
    public async Task Configured_N2_And_T1V_Bound_The_Connect_Backstop()
    {
        // With N2 and T1V both honoured through establishment, a connect to a peer
        // that never answers resolves quickly — bounded by (N2+1)·T1V, here
        // (1+1)·200 ms — instead of the 66 s spec maximum. (It surfaces as the SDL
        // exhausting its retries → link reset → InvalidOperationException, or as the
        // ConnectAsync deadline → TimeoutException; either way it fails fast, which
        // is the budget the node-test flake hinged on, #47.)
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 1,
            T1V = TimeSpan.FromMilliseconds(200),
        });
        await listener.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = async () => await listener.ConnectAsync(PeerCallA);
        // Either fast-failure mode is fine — the point is it does NOT burn 66 s.
        (await act.Should().ThrowAsync<Exception>())
            .Which.Should().Match(ex => ex is TimeoutException || ex is InvalidOperationException);
        sw.Stop();

        // Generous ceiling (CI scheduling jitter) but far under the 66 s spec max.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the connect backstop must honour the configured N2/T1V, not the 66 s spec default");
    }

    // ─── T2 / k(mod-8) establishment seeds (the #292/#300 clobber class) ─────
    //
    // Audit (m0lte/packet.net) of the establishment path for OTHER configured
    // per-port params that a hard-coded establishment constant could clobber, the
    // same way figc4.x's `SRT := Initial Default` clobbered T1V (#292) and `N2 := 10`
    // clobbered N2 (#300). The figc4.7 `Set_Version` bodies carry `T2 := 3000` and
    // (mod-8) `k := 8` as the remaining hard-coded init verbs. In the current
    // Packet.Ax25.Sdl package the data-link establishment path does NOT invoke
    // Set_Version as a subroutine, so a configured T2 / k already survives a
    // connect — these tests pin that survival as a behavioural regression — and the
    // verbs now read a configurable seed (InitialT2 / InitialK) defaulting to the
    // spec value, so the survival is structural rather than incidental and a future
    // SDL revision that re-introduces Set_Version on the connect path cannot
    // re-open the clobber. Default-stays-spec is covered in ActionDispatcherTests.

    [Fact]
    public async Task Configured_T2_Survives_The_Inbound_Accept_Handshake()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            T2 = TimeSpan.FromMilliseconds(4000),
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));

        session.Context.T2.Should().Be(TimeSpan.FromMilliseconds(4000),
            "the configured T2 must survive the figc4.1 SABM-accept establishment path");
    }

    [Fact]
    public async Task Default_T2_Is_The_Spec_Value_After_Establishment()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));

        session.Context.T2.Should().Be(TimeSpan.FromMilliseconds(3000),
            "with no override the session keeps the spec-default T2 (no silent default change, §2)");
    }

    [Fact]
    public async Task Configured_Window_K_Survives_The_Outbound_Connect_Handshake()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            K = 6,
        });
        await listener.StartAsync();

        var connectTask = listener.ConnectAsync(PeerCallA);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2)); // SABM
        modem.InjectInbound(Ax25Frame.Ua(LocalCall, PeerCallA, finalBit: true));

        var session = await connectTask.WithTimeout(TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
        session.Context.K.Should().Be(6,
            "the configured mod-8 window k must survive the figc4.2 outbound-connect establishment path");
    }

    [Fact]
    public async Task Default_Window_K_Is_The_Spec_Value_After_Establishment()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));

        session.Context.K.Should().Be(4,
            "with no override the mod-8 session keeps the spec-default window k=4 (no silent default change, §2)");
    }
}
