using System.Buffers.Binary;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Node.Core.Api;

/// <summary>
/// The minimal seam the connectionless-TEST pinger ("axping") correlates against: a
/// way to <em>send</em> a TEST command frame, and the <em>frame-trace stream</em> to
/// watch for the matching TEST response on. <see cref="Ax25Listener"/> satisfies this
/// directly (see <see cref="ListenerAxPingChannel"/>); a test fake can satisfy it
/// without a live radio, so the correlation core in <see cref="AxPinger"/> is
/// unit-testable without standing up a modem.
/// </summary>
/// <remarks>
/// The pinger only ever READS via <see cref="FrameTraced"/> and calls the public
/// <see cref="SendTestAsync"/> - it never mutates the supervisor's live port set, so it
/// does not need (and must not take) the host's <c>RunExclusiveAsync</c> gate.
/// </remarks>
public interface IAxPingChannel
{
    /// <summary>This station's identity (the TEST command's source callsign).</summary>
    Callsign MyCall { get; }

    /// <summary>The per-frame TX/RX trace stream - the same event
    /// <see cref="Ax25Listener.FrameTraced"/> raises. The pinger subscribes here to
    /// catch the peer's TEST response.</summary>
    event EventHandler<Ax25FrameEventArgs>? FrameTraced;

    /// <summary>Send a connectionless TEST command frame (P bit set) to
    /// <paramref name="destination"/> carrying <paramref name="info"/> - the probe whose
    /// echo the pinger correlates. Mirrors <see cref="Ax25Listener.SendTestAsync"/>.</summary>
    Task SendTestAsync(Callsign destination, ReadOnlyMemory<byte> info, CancellationToken ct = default);
}

/// <summary>
/// Adapts a live <see cref="Ax25Listener"/> to <see cref="IAxPingChannel"/> - the
/// production channel the endpoint hands the pinger. Pure delegation; holds no state.
/// </summary>
public sealed class ListenerAxPingChannel(Ax25Listener listener) : IAxPingChannel
{
    private readonly Ax25Listener listener = listener ?? throw new ArgumentNullException(nameof(listener));

    /// <inheritdoc/>
    public Callsign MyCall => listener.MyCall;

    /// <inheritdoc/>
    public event EventHandler<Ax25FrameEventArgs>? FrameTraced
    {
        add => listener.FrameTraced += value;
        remove => listener.FrameTraced -= value;
    }

    /// <inheritdoc/>
    public Task SendTestAsync(Callsign destination, ReadOnlyMemory<byte> info, CancellationToken ct = default)
        => listener.SendTestAsync(destination, info, pollFinal: true, ct);
}

/// <summary>
/// The connectionless AX.25 TEST ping ("axping"): sends N TEST command frames to a
/// target station and correlates each one's TEST response off the channel's
/// <see cref="IAxPingChannel.FrameTraced"/> stream to measure round-trip time. A
/// spec-compliant peer (§4.3.4.2) echoes the TEST command's information field in a TEST
/// response; a peer that doesn't implement TEST simply never answers - every probe times
/// out (loss 100%), which is a normal result, not an error.
/// </summary>
/// <remarks>
/// <para>
/// <b>Match predicate.</b> A traced frame matches a probe iff it is
/// <see cref="FrameDirection.Received"/>, is a TEST frame
/// (<c>(Control &amp; 0xEF) == 0xE3</c>, P/F-masked), is a <em>response</em>
/// (<see cref="Ax25Frame.IsResponse"/>), its <see cref="Ax25Frame.Source"/> callsign is
/// the target, AND its information field sequence-equals the probe's unique tag. The tag
/// makes a stray or duplicate TEST unable to false-match a probe.
/// </para>
/// <para>
/// <b>Timing.</b> All timing rides the injected <see cref="TimeProvider"/> - the timeout
/// is a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> and the RTT
/// is <see cref="Stopwatch.GetElapsedTime(long, long)"/> over
/// <see cref="TimeProvider.GetTimestamp"/> samples. No wall-clock (repo rule §2.7), so a
/// <c>FakeTimeProvider</c> drives both deterministically in tests.
/// </para>
/// <para>
/// <b>Ordering.</b> Pings are sequential (one outstanding at a time, axping-style). The
/// <see cref="IAxPingChannel.FrameTraced"/> handler is subscribed BEFORE the send so a
/// fast echo (loopback / net-sim) can't arrive before the correlator is listening.
/// </para>
/// </remarks>
public static class AxPinger
{
    // Process-unique probe-tag prefix: "PDNPING" + the process-unique nonce. The per-run
    // Guid + the per-probe seq are appended so no two probes - across runs, across
    // concurrent endpoints, or vs a stray TEST a peer left on the air - share a tag.
    private static readonly byte[] TagMagic = "PDNPING"u8.ToArray();

    /// <summary>
    /// Run a TEST ping against <paramref name="target"/> over <paramref name="channel"/>:
    /// <paramref name="count"/> sequential probes, each bounded by
    /// <paramref name="perPingTimeout"/>, timed on <paramref name="clock"/>.
    /// </summary>
    /// <param name="channel">The send + frame-trace seam (a live listener in production,
    /// a fake in tests).</param>
    /// <param name="target">The station to ping.</param>
    /// <param name="count">Number of probes (caller clamps to a sane range).</param>
    /// <param name="perPingTimeout">How long to wait for each probe's echo before
    /// recording it as a timeout.</param>
    /// <param name="clock">Time source for the timeout + RTT (repo rule §2.7).</param>
    /// <param name="ct">Cancellation for the whole run.</param>
    /// <returns>The aggregated <see cref="PingResult"/>. All-timeout (a peer that never
    /// answers) yields <see cref="PingResult.LossPct"/> 100 and is a normal result.</returns>
    public static async Task<PingResult> RunAsync(
        IAxPingChannel channel,
        Callsign target,
        int count,
        TimeSpan perPingTimeout,
        TimeProvider clock,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // One process-unique nonce per run - pinned ahead of the loop so the tag for every
        // probe in this run shares it; the per-probe seq distinguishes probes within the run.
        var runNonce = Guid.NewGuid();

        var replies = new List<PingReply>(count);
        for (int seq = 0; seq < count; seq++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await PingOnceAsync(channel, target, runNonce, seq, perPingTimeout, clock, ct)
                .ConfigureAwait(false);
            replies.Add(reply);
        }

        return Aggregate(replies, count);
    }

    private static async Task<PingReply> PingOnceAsync(
        IAxPingChannel channel,
        Callsign target,
        Guid runNonce,
        int seq,
        TimeSpan perPingTimeout,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tag = BuildTag(runNonce, seq);
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrame(object? sender, Ax25FrameEventArgs e)
        {
            if (IsMatchingResponse(e, target, tag))
            {
                matched.TrySetResult();
            }
        }

        // Subscribe BEFORE the send so a fast echo isn't missed.
        channel.FrameTraced += OnFrame;
        try
        {
            // Arm the per-probe timeout BEFORE sending. The timer (a Task.Delay over the
            // injected clock) must be REGISTERED before the probe becomes observably
            // "sent", because under a FakeTimeProvider a test advances the clock once it
            // sees the send - and an advance that lands before the timer exists is simply
            // lost (no timer to fire), so the probe would never time out and the run would
            // hang. Creating the delay first closes that race deterministically; the
            // sub-microsecond gap before the actual send is immaterial against a
            // multi-second real timeout (and the RTT clock starts at the send below, so
            // timing is unaffected). On the send-failure early-return the `using` cancels
            // this delay (an unawaited cancelled Task.Delay raises nothing).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = Task.Delay(perPingTimeout, clock, timeoutCts.Token);

            // Capture the RTT start as close to the send as possible (after the timeout
            // is armed, before the send).
            long startTs = clock.GetTimestamp();
            try
            {
                await channel.SendTestAsync(target, tag, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The port went away mid-run (evicted / disposed) or the modem write
                // failed - treat this probe as lost rather than tearing down the run.
                return new PingReply(seq, RttMs: null, Timeout: true);
            }

            var winner = await Task.WhenAny(matched.Task, timeout).ConfigureAwait(false);

            if (winner == matched.Task)
            {
                // Cancel the delay so its timer doesn't linger.
                await timeoutCts.CancelAsync().ConfigureAwait(false);
                // Use the TimeProvider's own GetElapsedTime - it converts the timestamp
                // delta with the provider's TimestampFrequency, so a FakeTimeProvider reads
                // back the exact advanced span (Stopwatch.GetElapsedTime would divide by the
                // machine's Stopwatch.Frequency, mismatching the fake's frequency).
                var rtt = clock.GetElapsedTime(startTs, clock.GetTimestamp());
                int rttMs = (int)Math.Round(rtt.TotalMilliseconds, MidpointRounding.AwayFromZero);
                if (rttMs < 0)
                {
                    rttMs = 0;   // clock skew belt-and-braces; never report a negative RTT
                }
                return new PingReply(seq, rttMs, Timeout: false);
            }

            // The delay won - but honour an explicit caller-cancel over a "timeout".
            ct.ThrowIfCancellationRequested();
            return new PingReply(seq, RttMs: null, Timeout: true);
        }
        finally
        {
            channel.FrameTraced -= OnFrame;
        }
    }

    // PDNPING magic | 16-byte run Guid | 4-byte big-endian seq. UTF-8 magic so it reads
    // sensibly in a monitor hex/ASCII dump; the Guid+seq make it collision-proof against
    // a stray/duplicate TEST and against a concurrent run.
    private static byte[] BuildTag(Guid runNonce, int seq)
    {
        var tag = new byte[TagMagic.Length + 16 + 4];
        var span = tag.AsSpan();
        TagMagic.CopyTo(span);
        runNonce.TryWriteBytes(span.Slice(TagMagic.Length));
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(TagMagic.Length + 16), seq);
        return tag;
    }

    // True iff this traced frame is the TEST response that echoes `tag` back from `target`.
    private static bool IsMatchingResponse(Ax25FrameEventArgs e, Callsign target, ReadOnlySpan<byte> tag)
    {
        if (e.Direction != FrameDirection.Received)
        {
            return false;
        }
        var frame = e.Frame;
        // TEST frame: U-frame base control octet (P/F masked) == 0xE3 (§4.3.4.2).
        if ((frame.Control & 0xEF) != 0xE3)
        {
            return false;
        }
        // Must be a RESPONSE (the peer's echo), not another station's TEST command.
        if (!frame.IsResponse)
        {
            return false;
        }
        if (!frame.Source.Callsign.Equals(target))
        {
            return false;
        }
        // The echoed info field must be byte-for-byte our unique probe tag.
        return frame.Info.Span.SequenceEqual(tag);
    }

    // Aggregate replies → PingResult. min/avg/max over the SUCCESSFUL replies only (0 when
    // none succeeded); lossPct = timeouts / count * 100.
    private static PingResult Aggregate(IReadOnlyList<PingReply> replies, int count)
    {
        int min = int.MaxValue, max = 0;
        long sum = 0;
        int ok = 0;
        foreach (var r in replies)
        {
            if (r is { Timeout: false, RttMs: { } rtt })
            {
                ok++;
                sum += rtt;
                if (rtt < min)
                {
                    min = rtt;
                }

                if (rtt > max)
                {
                    max = rtt;
                }
            }
        }

        int minMs = ok > 0 ? min : 0;
        int maxMs = ok > 0 ? max : 0;
        int avgMs = ok > 0 ? (int)Math.Round((double)sum / ok, MidpointRounding.AwayFromZero) : 0;
        int timeouts = count - ok;
        double lossPct = count > 0 ? (double)timeouts / count * 100.0 : 0.0;

        return new PingResult(replies, minMs, avgMs, maxMs, lossPct);
    }
}
