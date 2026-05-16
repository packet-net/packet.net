using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Unit tests for <see cref="Ax25Listener"/> — the first-class inbound
/// session acceptor. Each test wires a <see cref="LoopbackModem"/> in
/// place of a real KISS modem so the test owns both ends of the wire.
/// Inbound SABM/UA/DISC sequences are injected by writing the bytes
/// the peer would send; the listener parses, classifies, and dispatches
/// them, and the test observes the listener's events + the modem's
/// outbound queue.
/// </summary>
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
        await WaitFor(() => first.CurrentState == "Disconnected", TimeSpan.FromSeconds(2));

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

        var traced = new List<(Packet.Ax25.Session.FrameDirection Dir, Ax25Frame Frame)>();
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
        await WaitFor(() =>
        {
            lock (gate) return traced.Count >= 4;
        }, TimeSpan.FromSeconds(2));

        lock (gate)
        {
            traced.Count.Should().BeGreaterThanOrEqualTo(4);
            traced.Count(t => t.Dir == Packet.Ax25.Session.FrameDirection.Received).Should().BeGreaterThanOrEqualTo(2);
            traced.Count(t => t.Dir == Packet.Ax25.Session.FrameDirection.Transmitted).Should().BeGreaterThanOrEqualTo(2);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static async Task WaitFor(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"condition did not become true within {budget}");
    }

    /// <summary>
    /// In-memory <see cref="IKissModem"/> whose inbound stream is a
    /// channel the test writes <see cref="KissFrame"/>s into. Outbound
    /// <c>SendFrameAsync</c> appends to <see cref="SentFrames"/> for the
    /// test to assert against.
    /// </summary>
    private sealed class LoopbackModem : IKissModem
    {
        private readonly Channel<KissFrame> rx = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        public ObservableList<ReadOnlyMemory<byte>> SentFrames { get; } = new();

        public void InjectInbound(Ax25Frame frame)
        {
            var kf = new KissFrame((byte)0, KissCommand.Data, frame.ToBytes().ToArray());
            rx.Writer.TryWrite(kf);
        }

        public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
        {
            SentFrames.Add(ax25Bytes);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (rx.Reader.TryRead(out var f))
                {
                    yield return f;
                }
            }
        }

        public Task<AckModeReceipt> SendFrameWithAckAsync(
            ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetTxDelayAsync(byte v, CancellationToken c = default) => Task.CompletedTask;
        public Task SetPersistenceAsync(byte v, CancellationToken c = default) => Task.CompletedTask;
        public Task SetSlotTimeAsync(byte v, CancellationToken c = default) => Task.CompletedTask;
        public Task SetTxTailAsync(byte v, CancellationToken c = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Tiny thread-safe list with a "wait until count reaches N" helper.
    /// Lets tests block deterministically on the modem's outbound queue
    /// without polling sleeps littered through the assertions.
    /// </summary>
    private sealed class ObservableList<T>
    {
        private readonly List<T> items = new();
        private readonly object gate = new();
        private readonly List<TaskCompletionSource<bool>> waiters = new();

        public void Add(T item)
        {
            List<TaskCompletionSource<bool>> toComplete;
            lock (gate)
            {
                items.Add(item);
                toComplete = waiters.ToList();
                waiters.Clear();
            }
            foreach (var w in toComplete) w.TrySetResult(true);
        }

        public int Count
        {
            get { lock (gate) return items.Count; }
        }

        public T this[int i]
        {
            get { lock (gate) return items[i]; }
        }

        public List<T> SnapshotList()
        {
            lock (gate) return items.ToList();
        }

        public async Task WaitForCountAsync(int target, TimeSpan budget)
        {
            var deadline = DateTimeOffset.UtcNow + budget;
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (items.Count >= target) return;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add(tcs);
                    wait = tcs.Task;
                }
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"only {Count}/{target} items after {budget}");
                var done = await Task.WhenAny(wait, Task.Delay(remaining));
                if (done != wait) throw new TimeoutException($"only {Count}/{target} items after {budget}");
            }
        }
    }
}

internal static class ListenerTestTaskExtensions
{
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan budget)
    {
        var done = await Task.WhenAny(task, Task.Delay(budget));
        if (done != task) throw new TimeoutException($"task did not complete within {budget}");
        return await task;
    }

    public static async Task WithTimeout(this Task task, TimeSpan budget)
    {
        var done = await Task.WhenAny(task, Task.Delay(budget));
        if (done != task) throw new TimeoutException($"task did not complete within {budget}");
        await task;
    }
}
