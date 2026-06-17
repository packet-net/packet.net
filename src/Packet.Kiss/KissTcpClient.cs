using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Packet.Kiss;

/// <summary>
/// A simple TCP client that speaks KISS to a peer (typically a TNC or a
/// node like LinBPQ that exposes a KISS-over-TCP listener). Handles framing
/// in both directions so callers deal in <see cref="KissFrame"/>s, not
/// escape sequences.
/// </summary>
/// <remarks>
/// <para>
/// Supports the G8BPQ ACKMODE extension (KISS command 0x0C) via
/// <see cref="SendFrameWithAckAsync"/>: the host writes an ACKMODE send frame
/// tagged with a 16-bit sequence and awaits the TNC's matching TX-completion
/// echo. The echo is intercepted on the RX path (see
/// <see cref="ReadAvailableAsync"/>) and never surfaces to frame consumers;
/// ordinary inbound frames pass through unchanged. Plain
/// <see cref="SendFrameAsync"/> stays fire-and-forget (KISS Data, cmd 0x00)
/// and is unaffected — ACKMODE is opt-in.
/// </para>
/// <para>
/// Sends and the RX pump run on different tasks concurrently. Pending-ack
/// state lives in a <see cref="ConcurrentDictionary{TKey,TValue}"/> and the
/// completions use <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>,
/// so completing a waiter from the RX pump never runs caller continuations
/// inline on the read loop. This mirrors the NinoTNC driver's ACKMODE
/// behaviour so the live transport stack behaves identically across modems.
/// </para>
/// </remarks>
public sealed class KissTcpClient : IKissModem, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Default ACKMODE timeout when the caller doesn't supply one. Matches
    /// <see cref="AdaptiveKissTransport.DefaultAckTimeout"/> (and the NinoTNC
    /// driver default) so the live transport stack behaves consistently
    /// whether or not the adaptive layer threads a timeout through.
    /// </summary>
    private static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default read-idle budget. A half-open TCP connection — the peer rebooted,
    /// the cable was pulled, net-sim was restarted — sends no FIN, so a plain
    /// <c>ReadAsync</c> blocks forever and the port silently dies (#464). If no
    /// byte arrives within this window the link is treated as dead and the read
    /// stream ends, which drives <c>ReconnectingKissModem</c> to re-dial. The
    /// window is generous because a healthy packet-radio link is often quiet for
    /// long stretches between frames; OS keepalive (enabled on real sockets) is
    /// the faster probe, this is the backstop that also covers stacks/peers that
    /// drop keepalive probes. <see cref="TimeSpan.Zero"/> (or
    /// <see cref="Timeout.InfiniteTimeSpan"/>) disables idle detection entirely
    /// — the pre-#464 block-forever behaviour.
    /// </summary>
    public static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly TcpClient? tcp;
    private readonly Stream stream;
    private readonly KissDecoder decoder = new();
    private readonly byte[] readBuffer = new byte[4096];
    private readonly TimeProvider time;

    // How long a read may stall before the link is presumed dead. Zero or
    // Timeout.InfiniteTimeSpan disables the check (block forever, as before).
    private readonly TimeSpan readIdleTimeout;

    // In-flight ACKMODE sends, keyed by 16-bit sequence tag. The RX pump
    // completes the matching waiter when the TNC echoes the tag back; the
    // send path inserts on submit and removes on timeout/cancellation. Both
    // run concurrently, hence the concurrent map.
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<AckModeReceipt>> pendingAcks = new();

    // Auto-assigned tag cursor for callers that don't choose their own. Bumped
    // with Interlocked and masked to 16 bits; 0 is skipped so an auto tag is
    // always non-zero (callers may legitimately use 0 explicitly).
    private int ackSequenceCursor;

    private KissTcpClient(TcpClient tcp, TimeSpan readIdleTimeout, TimeProvider? timeProvider)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
        time = timeProvider ?? TimeProvider.System;
        this.readIdleTimeout = readIdleTimeout;
        EnableTcpKeepAlive(tcp);
    }

    // Stream-injecting ctor for tests: drives the same read/write/ackmode
    // logic over an arbitrary duplex stream (e.g. a loopback pipe) without a
    // real socket. Not part of the public surface.
    internal KissTcpClient(Stream stream, TimeSpan? readIdleTimeout = null, TimeProvider? timeProvider = null)
    {
        tcp = null;
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        time = timeProvider ?? TimeProvider.System;
        this.readIdleTimeout = readIdleTimeout ?? Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Connect to a KISS-over-TCP listener at <paramref name="host"/>:<paramref name="port"/>.
    /// Uses <see cref="DefaultReadIdleTimeout"/> for half-open-link detection
    /// (#464). For an explicit idle timeout or a test clock, use
    /// <see cref="ConnectAsync(string,int,System.TimeSpan?,System.TimeProvider?,System.Threading.CancellationToken)"/>.
    /// </summary>
    public static Task<KissTcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        => ConnectAsync(host, port, readIdleTimeout: null, timeProvider: null, cancellationToken);

    /// <summary>
    /// Connect to a KISS-over-TCP listener, controlling the read-idle liveness
    /// timeout that lets a supervisor reconnect after a half-open drop (#464).
    /// </summary>
    /// <param name="host">Listener host.</param>
    /// <param name="port">Listener TCP port.</param>
    /// <param name="readIdleTimeout">
    /// How long a read may stall with no inbound byte before the link is presumed
    /// dead and the inbound stream ends (so a supervisor can reconnect). Null uses
    /// <see cref="DefaultReadIdleTimeout"/>; <see cref="Timeout.InfiniteTimeSpan"/>
    /// or <see cref="TimeSpan.Zero"/> disables idle detection (block forever, the
    /// pre-#464 behaviour).
    /// </param>
    /// <param name="timeProvider">Clock for the idle timeout (test seam).</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<KissTcpClient> ConnectAsync(
        string host,
        int port,
        TimeSpan? readIdleTimeout,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new KissTcpClient(tcp, readIdleTimeout ?? DefaultReadIdleTimeout, timeProvider);
    }

    // Ask the OS to probe a quiet peer so a half-open connection (peer rebooted
    // without a FIN) surfaces as a read error in bounded time rather than hanging
    // forever. Best-effort: keepalive knobs are platform-dependent and a failure
    // to set them is non-fatal — the read-idle timeout is the portable backstop.
    private static void EnableTcpKeepAlive(TcpClient tcp)
    {
        try
        {
            var socket = tcp.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Start probing after 30 s idle, every 10 s, give up after ~5 missed
            // probes. Values are advisory; unsupported options are swallowed.
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveTime, 30);
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveInterval, 10);
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch
        {
            // best-effort; the read-idle timeout still detects a dead link
        }
    }

    private static void TrySetSocketOption(Socket socket, SocketOptionName name, int value)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, name, value); }
        catch { /* not supported on this platform/stack */ }
    }

    /// <summary>
    /// Send a KISS frame to the peer.
    /// </summary>
    public async Task SendAsync(byte port, KissCommand command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var encoded = KissEncoder.Encode(port, command, payload.Span);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read available bytes from the socket and return any KISS frames that
    /// have now completed. Returns an empty list if the socket has data
    /// pending but no frame finished yet — callers should loop.
    /// </summary>
    /// <remarks>
    /// ACKMODE TX-completion echoes are intercepted here: a decoded echo
    /// completes its pending waiter and is dropped from the returned list, so
    /// frame consumers never see it. All other frames pass through unchanged.
    /// This is the single chokepoint through which both <see cref="ReceiveAsync"/>
    /// and <see cref="ReadFramesAsync"/> produce frames.
    /// </remarks>
    public async Task<IReadOnlyList<KissFrame>> ReadAvailableAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await ReadWithIdleTimeoutAsync(cancellationToken).ConfigureAwait(false);
        if (bytesRead == 0)
        {
            throw new IOException("KISS-TCP peer closed the connection");
        }
        var frames = decoder.Push(readBuffer.AsSpan(0, bytesRead));
        return InterceptAckEchoes(frames);
    }

    // Read from the stream, but presume the link dead if no byte arrives within
    // the read-idle budget. A half-open TCP connection (peer rebooted, no FIN)
    // would otherwise block here forever and silently kill the port (#464); on
    // idle we throw IOException — the SAME signal a graceful close produces — so
    // ReadFramesAsync ends the stream and a supervisor reconnects. The caller's
    // own cancellation still propagates as OperationCanceledException untouched.
    private async Task<int> ReadWithIdleTimeoutAsync(CancellationToken cancellationToken)
    {
        if (readIdleTimeout <= TimeSpan.Zero || readIdleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
        }

        // Construct with the TimeProvider so a test clock drives the timeout
        // deterministically; the system clock is used in production.
        using var idle = new CancellationTokenSource(readIdleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        try
        {
            return await stream.ReadAsync(readBuffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Idle budget elapsed with no inbound byte: treat the half-open link
            // as dead so the reconnect machinery kicks in.
            throw new IOException(
                $"KISS-TCP link idle for {readIdleTimeout.TotalSeconds:0}s with no inbound data; presuming the peer is gone");
        }
    }

    // Pull ACKMODE TX-completion echoes out of a freshly-decoded batch: each
    // echo completes its waiter and is dropped; everything else is forwarded.
    // The common case (no pending acks) returns the original list untouched
    // with no allocation. A late/duplicate/unknown echo finds no waiter and
    // simply passes through as an ordinary frame.
    private IReadOnlyList<KissFrame> InterceptAckEchoes(IReadOnlyList<KissFrame> frames)
    {
        if (frames.Count == 0 || pendingAcks.IsEmpty)
        {
            return frames;
        }

        List<KissFrame>? passThrough = null;
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (KissAckMode.TryParseAcknowledgement(frame, out var tag) && pendingAcks.TryRemove(tag, out var waiter))
            {
                var now = DateTimeOffset.UtcNow;
                // Queued is stamped with the real submit time by the sender via
                // 'receipt with { Queued = ... }'; the RX pump only knows 'now'.
                waiter.TrySetResult(new AckModeReceipt(tag, now, now));
                passThrough ??= CopyUpTo(frames, i);
                continue;
            }
            passThrough?.Add(frame);
        }

        return passThrough ?? frames;
    }

    private static List<KissFrame> CopyUpTo(IReadOnlyList<KissFrame> frames, int exclusiveEnd)
    {
        var list = new List<KissFrame>(frames.Count - 1);
        for (int j = 0; j < exclusiveEnd; j++)
        {
            list.Add(frames[j]);
        }
        return list;
    }

    /// <summary>
    /// Read until at least one frame has been received, or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<IReadOnlyList<KissFrame>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frames = await ReadAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (frames.Count > 0)
            {
                return frames;
            }
        }
    }

    /// <summary>
    /// <see cref="IKissModem"/> shape: write a KISS-Data frame on
    /// port 0. Delegates to <see cref="SendAsync"/>.
    /// </summary>
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
        => SendAsync(port: 0, KissCommand.Data, ax25Bytes, cancellationToken);

    /// <summary>
    /// <see cref="IKissModem"/> shape: async stream of every inbound
    /// KISS frame until the socket closes or the token fires. Loops
    /// internally over <see cref="ReceiveAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<KissFrame> frames;
            try { frames = await ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            catch (IOException) { yield break; /* peer closed */ }
            foreach (var f in frames) yield return f;
        }
    }

    /// <summary>
    /// Send <paramref name="ax25Bytes"/> in ACKMODE (KISS command 0x0C) and
    /// await the TNC's TX-completion echo. Resolves with an
    /// <see cref="AckModeReceipt"/> carrying the round-trip timing when the
    /// echo arrives. Throws <see cref="TimeoutException"/> if no echo arrives
    /// within <paramref name="timeout"/> (default 30 s) and
    /// <see cref="OperationCanceledException"/> on caller cancellation — the
    /// same contract as the NinoTNC and serial drivers, so the adaptive layer
    /// records <see cref="Adaptive.FrameOutcome.AckModeTimedOut"/> uniformly.
    /// </summary>
    /// <param name="ax25Bytes">AX.25 frame to transmit.</param>
    /// <param name="timeout">Maximum time to wait for the echo. Defaults to 30 s.</param>
    /// <param name="sequenceTag">Caller-supplied 16-bit tag, or <c>null</c> to auto-assign.</param>
    /// <param name="cancellationToken">Cancels the wait (does not un-queue the frame at the TNC).</param>
    public async Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null, CancellationToken cancellationToken = default)
    {
        ushort tag = sequenceTag ?? NextSequenceTag();
        var waiter = new TaskCompletionSource<AckModeReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingAcks.TryAdd(tag, waiter))
        {
            throw new InvalidOperationException($"sequence tag 0x{tag:X4} already has a pending ACK; pick a unique tag");
        }

        var queuedAt = DateTimeOffset.UtcNow;
        try
        {
            var wire = KissAckMode.BuildSendFrame(port: 0, sequenceTag: tag, ax25Bytes.Span);
            await stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never leak the dictionary entry if the write itself failed.
            pendingAcks.TryRemove(tag, out _);
            throw;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultAckTimeout);
        await using var registration = cts.Token.Register(() =>
        {
            // On timeout/cancellation remove the entry (no leak) and fault the
            // waiter. A real echo that races in first wins via TryRemove in the
            // RX pump, so this no-ops.
            if (pendingAcks.TryRemove(tag, out var pending))
            {
                pending.TrySetException(cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException($"KISS-TCP peer did not echo ACKMODE tag 0x{tag:X4} within {timeout ?? DefaultAckTimeout}"));
            }
        }).ConfigureAwait(false);

        var receipt = await waiter.Task.ConfigureAwait(false);
        return receipt with { Queued = queuedAt };
    }

    // Roll the auto-assign cursor to the next non-zero 16-bit value.
    private ushort NextSequenceTag()
    {
        while (true)
        {
            int next = Interlocked.Increment(ref ackSequenceCursor) & 0xFFFF;
            if (next != 0)
            {
                return (ushort)next;
            }
        }
    }

    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)    => SendAsync(0, KissCommand.TxDelay,     new[] { tenMsUnits }, cancellationToken);
    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)     => SendAsync(0, KissCommand.Persistence, new[] { value },      cancellationToken);
    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)   => SendAsync(0, KissCommand.SlotTime,    new[] { tenMsUnits }, cancellationToken);
    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)     => SendAsync(0, KissCommand.TxTail,      new[] { tenMsUnits }, cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        FailPendingAcks(new ObjectDisposedException(nameof(KissTcpClient)));
        stream.Dispose();
        tcp?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        FailPendingAcks(new ObjectDisposedException(nameof(KissTcpClient)));
        await stream.DisposeAsync().ConfigureAwait(false);
        tcp?.Dispose();
    }

    // Fault any still-pending ack waiters on teardown so awaiting callers don't
    // hang past the connection's lifetime.
    private void FailPendingAcks(Exception cause)
    {
        foreach (var key in pendingAcks.Keys.ToArray())
        {
            if (pendingAcks.TryRemove(key, out var waiter))
            {
                waiter.TrySetException(cause);
            }
        }
    }
}
