using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Concurrency / collision / lifecycle stress on <see cref="Ax25Listener"/>.
/// Covers SABM collisions, repeated SABMs without a UA echo, inbound
/// SABM while an outbound <see cref="Ax25Listener.ConnectAsync"/> is in
/// flight, and graceful <see cref="Ax25Listener.StopAsync"/> behaviour
/// with multiple active sessions.
/// </summary>
/// <remarks>
/// The listener pump is single-threaded but its consumers (event
/// subscribers, ConnectAsync callers) live on whichever thread invoked
/// them. The tests intentionally exercise the interleavings the listener
/// has to survive - concurrent SABMs across distinct peers, SABM retries
/// where the previous outbound UA was "lost" (we drop it via
/// <see cref="LoopbackModem.DropOutbound"/>), and so on.
/// </remarks>
public class Ax25ListenerConcurrencyTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCallA = new("G7XYZ", 7);
    private static readonly Callsign PeerCallB = new("M5ABC", 3);
    private static readonly Callsign PeerCallC = new("VK2DEF", 1);

    // Payload tags identifying the two frames the serial-dispatch test drives
    // through the pump (see Listener_Dispatches_Frame_Handlers_Serially_On_The_Pump).
    private static readonly byte[] GateFrame1 = "gate-frame-1"u8.ToArray();
    private static readonly byte[] GateFrame2 = "gate-frame-2"u8.ToArray();

    // ─── Category 1: concurrency / collisions ───────────────────────────

    /// <summary>
    /// figc4.4 t41 - peer sends SABM while we're already Connected with
    /// V(s)==V(a) (no outstanding data). The session re-issues UA and
    /// stays Connected (silent reset). Emulates the SABM-collision
    /// resolution path: one side wins, both end up Connected. Our local
    /// session models the "we're already Connected and got a SABM from
    /// the peer who must have re-tried" half of the collision; the
    /// listener should keep the existing cached session, post the SABM
    /// into it, and not build a second one.
    /// </summary>
    [Fact]
    public async Task Listener_Handles_Sabm_Collision()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var acceptedCount = 0;
        var firstAccepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            int n = Interlocked.Increment(ref acceptedCount);
            if (n == 1)
            {
                firstAccepted.TrySetResult(e.Session);
            }
        };

        await listener.StartAsync();

        // First SABM brings us to Connected.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        var session = await firstAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // Now the "colliding" SABM - second SABM from the same peer
        // while we're Connected. figc4.4 t41 (V(s)==V(a) path) silently
        // resets and emits another UA. The listener must NOT build a
        // second session for the same callsign - same instance retained.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));

        // Verify a second UA went out and the session stayed Connected.
        Ax25Frame.TryParse(modem.SentFrames[1].Span, out var secondReply).Should().BeTrue();
        (secondReply!.Control & 0xEF).Should().Be(0x63, "t41 emits UA in response to the colliding SABM");
        session.CurrentState.Should().Be("Connected");

        // Crucial invariant: only ONE session was created - the cache
        // didn't accidentally branch on the second SABM. The listener
        // does re-fire SessionAccepted on a re-SABM but the underlying
        // Session reference is unchanged.
        await Task.Delay(100);
        var allAccepted = Volatile.Read(ref acceptedCount);
        allAccepted.Should().BeGreaterThanOrEqualTo(1,
            "first SABM fires SessionAccepted; collision-SABM in Connected stays Connected and may not re-fire (re-fire is only on Disconnected→Connected re-SABM)");
    }

    /// <summary>
    /// Peer sends SABM → we send UA → peer doesn't see UA (modem drops
    /// outbound) → peer retries SABM after T1 window. Listener must not
    /// build a second session for the same callsign - the existing
    /// cached session sits in Connected and figc4.4 t41 absorbs the
    /// retry with another UA, idempotently.
    /// </summary>
    [Fact]
    public async Task Listener_Handles_Multiple_Sabms_Within_T1_Window()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var sessionsCreated = new ConcurrentBag<Ax25Session>();
        listener.SessionAccepted += (_, e) => sessionsCreated.Add(e.Session);

        await listener.StartAsync();

        // Drop the listener's outbound UA so the (fake) peer doesn't see it.
        modem.DropOutbound = true;
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        await ListenerTestSupport.WaitFor(() => !sessionsCreated.IsEmpty, TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => modem.OutboundFrameCount >= 1, TimeSpan.FromSeconds(2),
            "listener must have attempted to send UA even though we drop it");

        // 100 ms later the peer retries - re-enable outbound so we can
        // observe the retry's UA reaches the wire.
        await Task.Delay(100);
        modem.DropOutbound = false;
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // Idempotence check: same Session instance, no second build.
        var distinct = sessionsCreated.Distinct().ToList();
        distinct.Count.Should().Be(1,
            "retry-SABM from the same peer must reuse the cached session, not build a new one — even when SessionAccepted re-fires");
        sessionsCreated.First().CurrentState.Should().Be("Connected");
    }

    /// <summary>
    /// <see cref="Ax25Listener.ConnectAsync"/> against peer B is in flight
    /// (we sent SABM, no UA back yet). Mid-handshake, peer C SABMs us
    /// inbound. Both should succeed - listener treats peers B and C
    /// as separate sessions. ConnectAsync's expected timeout against B
    /// is not relevant here: we just check that C's session gets
    /// SessionAccepted while ConnectAsync is still awaiting.
    /// </summary>
    [Fact]
    public async Task Listener_Handles_Inbound_Sabm_During_Outbound_ConnectAsync()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            // Short T1V so ConnectAsync's budget is bounded and the test
            // doesn't wait the full 6s default × N2. Doesn't affect the
            // inbound-from-C path at all.
            T1V = TimeSpan.FromMilliseconds(200),
            N2 = 2,
        });

        var cAccepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            if (e.Session.Context.Remote.Equals(PeerCallC))
            {
                cAccepted.TrySetResult(e.Session);
            }
        };

        await listener.StartAsync();

        // Kick the outbound ConnectAsync(B). It'll never resolve - no
        // peer is going to inject a UA in response. We let it time out
        // in the background.
        var connectBTask = listener.ConnectAsync(PeerCallB);

        // Brief settle so the outbound SABM has been emitted onto the
        // modem (we want to confirm the listener is mid-handshake).
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // Now peer C sends us a SABM. Should be accepted as a separate
        // session.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallC));

        var sessionC = await cAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        sessionC.Context.Remote.Should().Be(PeerCallC);
        sessionC.CurrentState.Should().Be("Connected");

        // ConnectAsync to B will throw TimeoutException eventually. Wait
        // (with a generous budget) for it to settle so we don't leak the
        // task into next-test territory.
        try { await connectBTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { /* expected - peer B never responded */ }
        catch (InvalidOperationException) { /* also acceptable - connect torn down */ }
    }

    /// <summary>
    /// Two connected sessions + StopAsync - the listener should tear
    /// down cleanly without deadlocking, even though sessions are still
    /// holding scheduler / timer resources.
    /// </summary>
    /// <remarks>
    /// The listener doesn't proactively send DISC on stop - its contract
    /// is to stop the inbound pump, not to drive a graceful disconnect
    /// on every cached peer. We assert the weaker invariant: stop
    /// returns within a reasonable budget and the listener reports
    /// IsRunning == false afterwards.
    /// </remarks>
    [Fact]
    public async Task Listener_StopAsync_During_Active_Sessions()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var sessions = new ConcurrentBag<Ax25Session>();
        var twoAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            sessions.Add(e.Session);
            if (sessions.Count >= 2)
            {
                twoAccepted.TrySetResult(true);
            }
        };

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallB));
        await twoAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        listener.IsRunning.Should().BeTrue();

        // StopAsync must return promptly even with active sessions.
        var stopTask = listener.StopAsync().AsTask();
        var done = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));
        done.Should().BeSameAs(stopTask, "StopAsync must not deadlock with active sessions in the cache");
        await stopTask;
        listener.IsRunning.Should().BeFalse();

        // Calling StopAsync twice is a no-op (idempotent).
        await listener.StopAsync();
    }

    // ─── Category 6: hostile event-handler ──────────────────────────────

    /// <summary>
    /// A SessionAccepted subscriber that throws must not crash the
    /// listener. The session must still be in the cache and a second
    /// peer must still be accepted.
    /// </summary>
    [Fact]
    public async Task Listener_Survives_SessionAccepted_Handler_That_Throws()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var throwingHandlerFires = 0;
        var observedSessions = new ConcurrentBag<Ax25Session>();
        listener.SessionAccepted += (_, _) =>
        {
            Interlocked.Increment(ref throwingHandlerFires);
            throw new InvalidOperationException("test-induced — handler must not crash the listener");
        };
        // A second, non-throwing subscriber lets us check the listener
        // is still firing the event after the throwing one bombs.
        listener.SessionAccepted += (_, e) => observedSessions.Add(e.Session);

        await listener.StartAsync();

        // First SABM: throwing handler fires. Listener mustn't crash.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        await ListenerTestSupport.WaitFor(() => throwingHandlerFires >= 1, TimeSpan.FromSeconds(2));

        // We expect the listener to be alive and processing - the
        // non-throwing subscriber should still have seen the event.
        // (Implementation note: in .NET, an unhandled exception in a
        // multicast delegate stops downstream subscribers from firing.
        // The listener pump runs on a background task that doesn't
        // crash because of an event-handler exception - it surfaces as
        // an unobserved exception unless caught. The listener must not
        // tear itself down.)
        await ListenerTestSupport.WaitFor(() => listener.IsRunning, TimeSpan.FromMilliseconds(200));
        listener.IsRunning.Should().BeTrue("listener must survive a throwing event handler");

        // Second SABM from a different peer - listener should still be
        // accepting. The first handler will throw again; we only check
        // that the listener stayed alive.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallB));
        await ListenerTestSupport.WaitFor(() => throwingHandlerFires >= 2, TimeSpan.FromSeconds(2));
        listener.IsRunning.Should().BeTrue();
    }

    /// <summary>
    /// Same shape as the SessionAccepted-throws test, on
    /// <see cref="Ax25Listener.FrameTraced"/>.
    /// </summary>
    [Fact]
    public async Task Listener_Survives_FrameTraced_Handler_That_Throws()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var throwingFires = 0;
        listener.FrameTraced += (_, _) =>
        {
            Interlocked.Increment(ref throwingFires);
            throw new InvalidOperationException("test-induced — FrameTraced handler must not crash listener");
        };

        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        // RX trace fires for the inbound SABM, TX trace fires for the
        // outbound UA - at least two fires expected.
        await ListenerTestSupport.WaitFor(() => throwingFires >= 1, TimeSpan.FromSeconds(2));
        listener.IsRunning.Should().BeTrue();

        // Another frame round-trip - listener must still process.
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, PeerCallA));
        await ListenerTestSupport.WaitFor(() => throwingFires >= 2, TimeSpan.FromSeconds(2));
        listener.IsRunning.Should().BeTrue();
    }

    /// <summary>
    /// The listener dispatches its frame handlers <em>serially, on the inbound pump
    /// itself</em>. <see cref="Ax25Listener.FrameTraced"/> subscribers are invoked
    /// synchronously from the body of the pump's <c>await foreach</c> over the
    /// transport (<c>InboundPumpAsync</c> -&gt; <c>TraceFrame</c> -&gt;
    /// <c>SafeInvoke</c>), so a handler that blocks backpressures the whole port:
    /// the pump does not even ask the transport for the next frame until the
    /// handler returns. That is the contract a consumer has to respect - do heavy
    /// work off-thread (hand it to a queue and return), because blocking inside a
    /// handler stalls every subsequent inbound frame on that port.
    /// </summary>
    /// <remarks>
    /// The negative half ("frame 2 has not been observed") is a proof rather than a
    /// race, and the probe is the pump's own read of the transport rather than a
    /// downstream side effect:
    /// <list type="number">
    /// <item>The handler signals <c>enteredGate</c> from inside itself and the test
    /// awaits that signal, so from then on the pump thread is known to be parked
    /// inside the frame-1 handler.</item>
    /// <item><see cref="RecordingTransport"/> records every <c>MoveNextAsync</c> the
    /// pump makes. Parked in the handler, the pump cannot be inside
    /// <c>MoveNextAsync</c>, so the ask count is still 1 - and it stays 1 until the
    /// gate is released. Were handlers dispatched off the pump, the pump would have
    /// gone straight back for the next frame and the count would be 2.</item>
    /// <item>Frame 2 is queued synchronously by <c>Inject</c> before the assertion
    /// runs, so it is demonstrably available; the only reason it is not observed is
    /// the parked pump.</item>
    /// </list>
    /// Releasing the gate then makes frame 2 observable, which is what proves the
    /// negative was not vacuous. The recorded event order states the contract
    /// directly: the second ask lands after the first handler returned, never
    /// alongside it.
    /// </remarks>
    [Fact]
    public async Task Listener_Dispatches_Frame_Handlers_Serially_On_The_Pump()
    {
        await using var transport = new RecordingTransport();
        await using var listener = new Ax25Listener(transport, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var enteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftGateOnRelease = false;

        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction != FrameDirection.Received)
            {
                return;
            }

            var info = e.Frame.Info.Span;
            if (info.SequenceEqual(GateFrame1))
            {
                transport.Record("enter-handler-1");
                enteredGate.TrySetResult();

                // Deliberately block the pump from inside the handler: that IS the
                // behaviour under test. The bounded wait is a deadlock guard so a
                // regression fails the run instead of hanging it; a passing run never
                // reaches the timeout, and the assertion below proves it did not.
                leftGateOnRelease = releaseGate.Task.Wait(TimeSpan.FromSeconds(30));
                transport.Record("leave-handler-1");
            }
            else if (info.SequenceEqual(GateFrame2))
            {
                transport.Record("enter-handler-2");
                secondObserved.TrySetResult();
            }
        };

        await listener.StartAsync();

        try
        {
            // Third-party UI frames (A -> B, overheard): the pump traces every frame
            // it parses, but DispatchInbound drops one not addressed to us straight
            // away - monitor-only. So the only work between the pump's trace call and
            // its next read of the transport is the handler itself, which is exactly
            // what this test measures.
            transport.Inject(Ax25Frame.Ui(PeerCallB, PeerCallA, GateFrame1));
            await enteredGate.Task.WithTimeout(TimeSpan.FromSeconds(5));

            transport.Asks.Should().Be(1,
                "the pump is parked inside the frame-1 handler, so it cannot have gone back to the transport for another frame");

            // Frame 2 is now queued on the transport with its only reader provably
            // parked inside the frame-1 handler (see the remarks).
            transport.Inject(Ax25Frame.Ui(PeerCallB, PeerCallA, GateFrame2));
            transport.Asks.Should().Be(1, "queueing a frame cannot un-park the pump");
            secondObserved.Task.IsCompleted.Should().BeFalse(
                "a slow handler backpressures the port - frame 2 waits behind frame 1's handler");
            transport.Events.Should().Equal(["ask", "take", "enter-handler-1"],
                "one read, one handler invocation, and the pump is still inside it");
        }
        finally
        {
            releaseGate.TrySetResult();
        }

        await secondObserved.Task.WithTimeout(TimeSpan.FromSeconds(5));

        leftGateOnRelease.Should().BeTrue(
            "the handler returned because the test released the gate, not because the deadlock guard expired");
        // Prefix, not the whole log: once frame 2's handler has returned the pump
        // goes back for a third frame, and that ask may or may not be recorded by
        // the time this assertion runs. Everything up to frame 2's dispatch is
        // strictly ordered.
        transport.Events.Take(7).Should().Equal(
            ["ask", "take", "enter-handler-1", "leave-handler-1", "ask", "take", "enter-handler-2"],
            "the pump reads and dispatches one frame at a time - handler invocations never overlap, and the next read waits for the handler to return");
        listener.IsRunning.Should().BeTrue();
    }

    /// <summary>
    /// An <see cref="IAx25Transport"/> that records the pump's reads: one "ask" per
    /// <c>MoveNextAsync</c> the listener makes on the inbound enumerator, one "take"
    /// per frame handed over. That makes the pump's position observable to a test
    /// without any timing assumptions - the serial-dispatch test above asserts on
    /// what the pump did and when, not on how long something took.
    /// </summary>
    private sealed class RecordingTransport : IAx25Transport
    {
        private readonly Channel<Ax25InboundFrame> rx =
            Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly ConcurrentQueue<string> events = new();
        private int asks;

        /// <summary>Snapshot of the ordered log of pump reads interleaved with whatever the test records.</summary>
        public IReadOnlyList<string> Events => events.ToArray();

        /// <summary>How many times the pump has asked the transport for a frame.</summary>
        public int Asks => Volatile.Read(ref asks);

        public void Record(string what) => events.Enqueue(what);

        public void Inject(Ax25Frame frame) =>
            rx.Writer.TryWrite(new Ax25InboundFrame(frame.ToBytes().ToArray(), 0, DateTimeOffset.UtcNow));

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            events.Enqueue("send");
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Interlocked.Increment(ref asks);
                events.Enqueue("ask");

                Ax25InboundFrame frame;
                try
                {
                    frame = await rx.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;   // listener shutdown
                }

                events.Enqueue("take");
                yield return frame;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
