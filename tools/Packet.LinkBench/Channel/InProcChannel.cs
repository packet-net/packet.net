using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Kiss;

namespace Packet.LinkBench.Channel;

/// <summary>Knobs for the in-process channel model (link-bench plan §3.1).</summary>
internal sealed record InProcChannelOptions
{
    /// <summary>Modeled signalling rate in bit/s. 0 disables airtime modelling
    /// entirely (rung 1: lossless, instant — pure engine flow control).</summary>
    public int Baud { get; init; }

    /// <summary>Keyup-to-data delay added to every transmission (KISS TXDELAY).</summary>
    public TimeSpan TxDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Post-data key-down hang (KISS TXTAIL).</summary>
    public TimeSpan TxTail { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>One transmitter at a time; a transmitter holds the medium for its
    /// whole airtime plus <see cref="Turnaround"/>.</summary>
    public bool HalfDuplex { get; init; }

    /// <summary>RX→TX turnaround tacked onto a half-duplex medium hold.</summary>
    public TimeSpan Turnaround { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Probability ∈ [0,1) that a transmitted frame is not delivered.
    /// (Frame-granularity loss; a bit-error rate collapses to this for fixed-size
    /// frames.) Loss never suppresses the ackmode echo — the frame transmitted
    /// fine, the receiver just didn't get it, exactly like RF.</summary>
    public double LossRate { get; init; }

    /// <summary>Seed for the loss roll — runs are repeatable.</summary>
    public int Seed { get; init; } = 42;

    /// <summary>Divide every modeled delay by this factor (and scale the engine's
    /// timers to match — the bench CLI does that) to run a slow channel faster
    /// than real time.</summary>
    public double TimeScale { get; init; } = 1.0;
}

/// <summary>
/// The in-process channel (link-bench plan §3.1): two <see cref="IAx25Transport"/>
/// endpoints joined by a channel model — per-frame airtime, optional
/// half-duplex, optional loss — that emits the ackmode TX-complete echo to the
/// sender <b>after the modeled airtime</b>, so ackmode pacing is exercised
/// meaningfully. This is the engine + ackmode + (later) DCD prototyping vehicle.
/// </summary>
internal sealed class InProcChannel : IBenchChannel
{
    // Flags + FCS framing the KISS payload doesn't carry but the air does.
    private const int FrameOverheadBytes = 4;

    private readonly InProcChannelOptions opts;
    private readonly SemaphoreSlim medium = new(1, 1);
    private readonly Random random;
    private readonly object randomGate = new();
    private int transmittersOnAir;

    public InProcChannel(InProcChannelOptions options)
    {
        opts = options;
        random = new Random(options.Seed);
        var a = new Endpoint(this, "A");
        var b = new Endpoint(this, "B");
        a.Peer = b;
        b.Peer = a;
        A = a;
        B = b;
    }

    public IAx25Transport EndpointA => A;
    public IAx25Transport EndpointB => B;
    public bool SupportsAckMode => true;

    internal Endpoint A { get; }
    internal Endpoint B { get; }

    /// <summary>DCD seam (plan §8) — the policy each endpoint's TX pump consults
    /// before keying up. No-op today; a CSMA-by-DCD policy plugs in here.</summary>
    public ITxGatePolicy TxGate { get; set; } = NoOpTxGatePolicy.Instance;

    /// <summary>
    /// DCD seam (plan §8), designed but deliberately not wired to the engine: the
    /// channel's busy (true) / clear (false) state, asserted while any endpoint is
    /// on the air. This is the modem→host signal the coming KISS DCD extension
    /// will carry — an unsolicited state change, distinct from frame-RX and from
    /// the ackmode TX-complete echo (which is a reply to MY send).
    /// </summary>
    public event EventHandler<bool>? ChannelStateChanged;

    private TimeSpan Scale(TimeSpan t) =>
        opts.TimeScale == 1.0 ? t : TimeSpan.FromTicks((long)(t.Ticks / opts.TimeScale));

    /// <summary>frameBits / baud + txdelay + txtail (plan §3.1); zero when airtime
    /// modelling is off.</summary>
    internal TimeSpan Airtime(int frameLength)
    {
        if (opts.Baud <= 0) return TimeSpan.Zero;
        var bits = (frameLength + FrameOverheadBytes) * 8;
        return Scale(opts.TxDelay + TimeSpan.FromSeconds(bits / (double)opts.Baud) + opts.TxTail);
    }

    private bool RollLoss()
    {
        if (opts.LossRate <= 0) return false;
        lock (randomGate)
        {
            return random.NextDouble() < opts.LossRate;
        }
    }

    /// <summary>One transmission: gate (DCD seam) → acquire the medium (half
    /// duplex) → airtime → deliver to the peer unless the loss roll eats it.
    /// Returns when the frame has cleared the air — the caller completes the
    /// ackmode echo on return.</summary>
    internal async Task TransmitAsync(Endpoint sender, byte[] frame, CancellationToken ct)
    {
        await TxGate.WaitForClearAsync(this, ct).ConfigureAwait(false);

        var held = false;
        if (opts.HalfDuplex)
        {
            await medium.WaitAsync(ct).ConfigureAwait(false);
            held = true;
        }

        try
        {
            if (Interlocked.Increment(ref transmittersOnAir) == 1)
            {
                ChannelStateChanged?.Invoke(this, true);
            }

            var airtime = Airtime(frame.Length);
            if (airtime > TimeSpan.Zero)
            {
                await Task.Delay(airtime, ct).ConfigureAwait(false);
            }

            if (!RollLoss())
            {
                sender.Peer.Deliver(frame);
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref transmittersOnAir) == 0)
            {
                ChannelStateChanged?.Invoke(this, false);
            }

            if (held)
            {
                var turnaround = Scale(opts.Turnaround);
                if (turnaround > TimeSpan.Zero)
                {
                    // Hold the medium through the turnaround so the other side
                    // can't key up instantly back-to-back.
                    try { await Task.Delay(turnaround, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* never let turnaround tear down the pump */ }
                }
                medium.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await A.DisposeAsync().ConfigureAwait(false);
        await B.DisposeAsync().ConfigureAwait(false);
        medium.Dispose();
    }

    /// <summary>
    /// One side's modem. TX is a queue + single pump — a radio transmits one
    /// frame at a time — so the engine's fire-and-forget sink serialises here
    /// just like a real TNC's buffer. <see cref="SendAwaitingCompletionAsync"/>
    /// resolves when the frame has cleared the modeled air: the ackmode echo.
    /// </summary>
    internal sealed class Endpoint : ITxCompletionTransport, ICsmaChannelParams, IAsyncDisposable
    {
        private readonly InProcChannel channel;
        private readonly Channel<TxJob> txQueue;
        private readonly Channel<Ax25InboundFrame> rx;
        private readonly CancellationTokenSource lifecycle = new();
        private readonly Task pump;
        private int disposed;

        private readonly record struct TxJob(
            byte[] Frame, DateTimeOffset Queued, TaskCompletionSource<DateTimeOffset>? TxComplete);

        internal Endpoint(InProcChannel channel, string name)
        {
            this.channel = channel;
            Name = name;
            txQueue = System.Threading.Channels.Channel.CreateUnbounded<TxJob>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            rx = System.Threading.Channels.Channel.CreateUnbounded<Ax25InboundFrame>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            pump = Task.Run(() => PumpAsync(lifecycle.Token));
        }

        public string Name { get; }
        internal Endpoint Peer { get; set; } = null!;

        internal void Deliver(byte[] frame) =>
            rx.Writer.TryWrite(new Ax25InboundFrame(frame, 0, DateTimeOffset.UtcNow));

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            // Snapshot — the listener's sink may reuse its buffer the instant
            // this returns (same contract PacingKissModem honours).
            txQueue.Writer.TryWrite(new TxJob(ax25.ToArray(), DateTimeOffset.UtcNow, null));
            return Task.CompletedTask;
        }

        public async Task<TxCompletion> SendAwaitingCompletionAsync(
            ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var queued = DateTimeOffset.UtcNow;
            var tcs = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!txQueue.Writer.TryWrite(new TxJob(ax25.ToArray(), queued, tcs)))
            {
                throw new ObjectDisposedException(nameof(Endpoint));
            }

            var wait = timeout is { } t ? Task.Delay(t, cancellationToken) : Task.Delay(Timeout.Infinite, cancellationToken);
            var winner = await Task.WhenAny(tcs.Task, wait).ConfigureAwait(false);
            if (winner != tcs.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"no TX-complete within {timeout!.Value.TotalMilliseconds:F0} ms.");
            }

            var acked = await tcs.Task.ConfigureAwait(false);
            return new TxCompletion(queued, acked);
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var job in txQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await channel.TransmitAsync(this, job.Frame, ct).ConfigureAwait(false);
                    job.TxComplete?.TrySetResult(DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal — drain anything still queued so ackmode waiters don't hang.
            }
            finally
            {
                while (txQueue.Reader.TryRead(out var leftover))
                {
                    leftover.TxComplete?.TrySetCanceled();
                }
            }
        }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in rx.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            txQueue.Writer.TryComplete();
            await lifecycle.CancelAsync().ConfigureAwait(false);
            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* normal */ }
            lifecycle.Dispose();
            rx.Writer.TryComplete();
        }
    }
}
