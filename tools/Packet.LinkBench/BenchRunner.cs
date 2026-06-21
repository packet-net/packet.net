using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.LinkBench.Channel;
using Packet.LinkBench.Metrics;
using Packet.Node.Core.Transports;

namespace Packet.LinkBench;

/// <summary>Outcome of one bench run (link-bench plan §5 metrics).</summary>
internal sealed record BenchResult
{
    public required RunConfig Config { get; init; }
    public bool Completed { get; init; }
    public string? Failure { get; init; }
    public bool IntegrityOk { get; init; }
    public string? IntegrityDetail { get; init; }
    public TimeSpan ConnectTime { get; init; }
    public TimeSpan TransferTime { get; init; }
    public double ThroughputBytesPerSec { get; init; }
    public bool CleanDisconnect { get; init; }
    public FrameStats StatsA { get; init; } = new();
    public FrameStats StatsB { get; init; } = new();
    public int AckReceipts { get; init; }
    public TimeSpan? AckRttMin { get; init; }
    public TimeSpan? AckRttMean { get; init; }
    public TimeSpan? AckRttMax { get; init; }
    public IReadOnlyList<TracedFrame> TraceA { get; init; } = [];
    public IReadOnlyList<TracedFrame> TraceB { get; init; } = [];
    public IReadOnlyList<(DateTimeOffset At, string Endpoint, string What)> Events { get; init; } = [];
}

/// <summary>
/// One rung-1 run: stand up two AX.25 engines on the configured channel,
/// connect A→B, stream the payload as connected-mode I-frames, drain on B,
/// verify integrity, clean DISC — and table what the wire saw.
/// </summary>
internal static class BenchRunner
{
    // Cycles the SSID per run so frames still draining through a slow shared
    // channel (net-sim audio in flight) from run N can't hit run N+1's
    // handshake — the address filter drops them as not-ours.
    private static int runCounter = -1;

    public static async Task<BenchResult> RunAsync(RunConfig cfg, CancellationToken ct)
    {
        var ssid = (byte)(Interlocked.Increment(ref runCounter) % 16);
        var callA = new Callsign("BENCHA", ssid);
        var callB = new Callsign("BENCHB", ssid);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(cfg.RunTimeout);

        await using var channel = await BuildChannelAsync(cfg, runCts.Token).ConfigureAwait(false);

        // Ackmode wiring: PacingKissModem serialises the engine's fire-and-forget
        // blast onto the channel one frame at a time, releasing each frame only on
        // the prior frame's TX-complete echo; the tap underneath records echo RTTs.
        var receipts = new List<AckModeReceipt>();
        var receiptsGate = new object();
        void Record(AckModeReceipt r)
        {
            lock (receiptsGate) { receipts.Add(r); }
        }

        var pacingTimeout = Scale(PacingKissModem.DefaultPacingTimeout, cfg.TimeScale);
        var modemA = cfg.AckMode ? new PacingKissModem(new AckReceiptTap(channel.EndpointA, Record), pacingTimeout) : channel.EndpointA;
        var modemB = cfg.AckMode ? new PacingKissModem(new AckReceiptTap(channel.EndpointB, Record), pacingTimeout) : channel.EndpointB;

        // Engine timer config. With a scaled channel, scale the timer defaults by
        // the same factor so T1:airtime and T2:airtime ratios survive the speedup.
        TimeSpan? t1 = cfg.T1 ?? (cfg.TimeScale != 1.0 ? Scale(TimeSpan.FromSeconds(6), cfg.TimeScale) : null);
        TimeSpan? t2 = cfg.T2 ?? (cfg.TimeScale != 1.0 ? Scale(TimeSpan.FromSeconds(3), cfg.TimeScale) : null);
        TimeSpan? t3 = cfg.TimeScale != 1.0 ? Scale(TimeSpan.FromSeconds(30), cfg.TimeScale) : null;

        var payloadAtoB = MakePayload(cfg.PayloadBytes, cfg.Seed * 31 + 1);
        var payloadBtoA = cfg.Bidirectional ? MakePayload(cfg.PayloadBytes, cfg.Seed * 31 + 2) : [];

        var sinkOnB = new ReceiveSink(payloadAtoB.Length);
        var sinkOnA = new ReceiveSink(payloadBtoA.Length);
        var discConfirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionOnB = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tapA = new FrameTap();
        var tapB = new FrameTap();

        // Session-event journal: DL signals (error letters, disconnects) and SDL
        // transitions, timestamped, for the --trace dump. This is how a wedge
        // gets diagnosed: the frame trace says WHAT hit the wire, this says WHY.
        var events = new List<(DateTimeOffset At, string Endpoint, string What)>();
        var eventsGate = new object();
        void Journal(string endpoint, string what)
        {
            lock (eventsGate) { events.Add((DateTimeOffset.UtcNow, endpoint, what)); }
        }
        List<(DateTimeOffset, string, string)> SnapshotEvents()
        {
            lock (eventsGate) { return [.. events]; }
        }
        void WireSessionJournal(Ax25Session s, string endpoint)
        {
            s.DataLinkSignalEmitted += (_, sig) =>
            {
                if (sig is DataLinkDataIndication) return; // too chatty; the byte counts cover it
                Journal(endpoint, sig is DataLinkErrorIndication err ? $"DL-ERROR {err.Code}" : sig.Name);
            };
            s.TransitionFired += (_, t) => Journal(endpoint, $"{t.From} → {t.Next} ({t.Id})");
        }

        var listenerA = new Ax25Listener(new Packet.Kiss.KissModemTransport(modemA), new Ax25ListenerOptions
        {
            MyCall = callA,
            K = cfg.K,
            T1V = t1,
            T2 = t2,
            T3 = t3,
            RestartT1OnTxComplete = cfg.T1FromTxComplete,
            ConfigureSession = s =>
            {
                s.DataLinkSignalEmitted += (_, sig) =>
                {
                    sinkOnA.OnSignal(sig);
                    if (sig is DataLinkDisconnectConfirm) discConfirm.TrySetResult();
                };
                WireSessionJournal(s, "A");
            },
        });
        var listenerB = new Ax25Listener(new Packet.Kiss.KissModemTransport(modemB), new Ax25ListenerOptions
        {
            MyCall = callB,
            K = cfg.K,
            T1V = t1,
            T2 = t2,
            T3 = t3,
            RestartT1OnTxComplete = cfg.T1FromTxComplete,
            ConfigureSession = s =>
            {
                s.DataLinkSignalEmitted += (_, sig) => sinkOnB.OnSignal(sig);
                WireSessionJournal(s, "B");
            },
        });

        tapA.Attach(listenerA);
        tapB.Attach(listenerB);
        listenerB.SessionAccepted += (_, e) => sessionOnB.TrySetResult(e.Session);

        try
        {
            await listenerA.StartAsync(runCts.Token).ConfigureAwait(false);
            await listenerB.StartAsync(runCts.Token).ConfigureAwait(false);

            // ── Connect ──
            var connectStarted = DateTimeOffset.UtcNow;
            Ax25Session session;
            try
            {
                session = await listenerA.ConnectAsync(callB, runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                return Fail(cfg, $"connect failed: {ex.Message}", tapA, tapB, receipts, receiptsGate, SnapshotEvents());
            }
            var connected = DateTimeOffset.UtcNow;

            // B's accepted session — always captured (needed for SREJ enablement
            // and for the bidirectional send). By the time A's ConnectAsync has
            // returned, B has sent its UA, so B is Connected and its establish
            // (which sets the v2.0 reject mode) has already run.
            Ax25Session bSession;
            try
            {
                bSession = await sessionOnB.Task.WaitAsync(runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail(cfg, "B never saw the inbound session", tapA, tapB, receipts, receiptsGate, SnapshotEvents());
            }

            // SREJ (selective reject): force it on BOTH ends, mirroring the engine's
            // own Set_Selective_Reject verb (the figc4.7 v2.2 default). Must be set
            // AFTER establish — a mod-8 connect runs Set_Version_2_0, which clears
            // SrejEnabled — so ConfigureSession (pre-connect) would be clobbered;
            // this is exactly how DataLinkSrejUnderLossTests stage it. SREJ only
            // changes behaviour under frame loss (the --loss knob, or net-sim
            // collisions): a clean run never produces an out-of-sequence frame, so
            // the selective/go-back-N choice never arises. Done before any I-frame
            // is queued (the SendChunked calls below), so no frame escapes pre-SREJ.
            if (cfg.Srej)
            {
                foreach (var ctx in new[] { session.Context, bSession.Context })
                {
                    ctx.ImplicitReject = false;
                    ctx.SrejEnabled = true;
                }
            }

            // ── Stream the payload(s) as connected-mode I-frames ──
            var transferStarted = DateTimeOffset.UtcNow;
            SendChunked(listenerA, session, payloadAtoB, cfg.Paclen);

            if (cfg.Bidirectional)
            {
                SendChunked(listenerB, bSession, payloadBtoA, cfg.Paclen);
            }

            // ── Drain ──
            var waits = new List<Task<DateTimeOffset>> { sinkOnB.Done };
            if (cfg.Bidirectional) waits.Add(sinkOnA.Done);
            DateTimeOffset lastByteAt;
            try
            {
                var doneTimes = await Task.WhenAll(waits).WaitAsync(runCts.Token).ConfigureAwait(false);
                lastByteAt = doneTimes.Max();
            }
            catch (OperationCanceledException)
            {
                return Fail(
                    cfg,
                    $"transfer timed out after {cfg.RunTimeout.TotalSeconds:F0}s " +
                    $"(B received {sinkOnB.BytesReceived}/{payloadAtoB.Length}" +
                    (cfg.Bidirectional ? $", A received {sinkOnA.BytesReceived}/{payloadBtoA.Length}" : "") + ")",
                    tapA, tapB, receipts, receiptsGate, SnapshotEvents());
            }

            var transferTime = lastByteAt - transferStarted;
            var integrity = sinkOnB.Matches(payloadAtoB) && (!cfg.Bidirectional || sinkOnA.Matches(payloadBtoA));
            string? integrityDetail = integrity ? null
                : "B: " + sinkOnB.Diagnose(payloadAtoB)
                  + (cfg.Bidirectional ? " | A: " + sinkOnA.Diagnose(payloadBtoA) : "");

            // ── Clean DISC from A ──
            session.PostEvent(new DlDisconnectRequest());
            var cleanDisc = false;
            try
            {
                await discConfirm.Task.WaitAsync(Scale(TimeSpan.FromSeconds(30), cfg.TimeScale), runCts.Token).ConfigureAwait(false);
                cleanDisc = true;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Transfer result stands; report the unclean teardown.
            }

            var totalPayload = (long)payloadAtoB.Length + payloadBtoA.Length;
            var (min, mean, max, n) = SummariseReceipts(receipts, receiptsGate);
            return new BenchResult
            {
                Config = cfg,
                Completed = true,
                IntegrityOk = integrity,
                IntegrityDetail = integrityDetail,
                ConnectTime = connected - connectStarted,
                TransferTime = transferTime,
                ThroughputBytesPerSec = transferTime > TimeSpan.Zero ? totalPayload / transferTime.TotalSeconds : 0,
                CleanDisconnect = cleanDisc,
                StatsA = FrameStats.Analyze(tapA.Snapshot(), cfg.EffectiveK, cfg.DupWindow),
                StatsB = FrameStats.Analyze(tapB.Snapshot(), cfg.EffectiveK, cfg.DupWindow),
                AckReceipts = n,
                AckRttMin = min,
                AckRttMean = mean,
                AckRttMax = max,
                TraceA = tapA.Snapshot(),
                TraceB = tapB.Snapshot(),
                Events = SnapshotEvents(),
            };
        }
        finally
        {
            // Stop the engines before their modems go away, then tear down the
            // modem stacks. The pacing decorators own their inner chain down to
            // the channel endpoint; endpoint disposal is idempotent, so the
            // channel's own DisposeAsync (the outermost `await using`) is safe
            // to run after.
            await listenerA.DisposeAsync().ConfigureAwait(false);
            await listenerB.DisposeAsync().ConfigureAwait(false);
            if (cfg.AckMode)
            {
                await ((IAsyncDisposable)modemA).DisposeAsync().ConfigureAwait(false);
                await ((IAsyncDisposable)modemB).DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IBenchChannel> BuildChannelAsync(RunConfig cfg, CancellationToken ct) =>
        cfg.Channel switch
        {
            "inproc" => new InProcChannel(new InProcChannelOptions
            {
                Baud = cfg.Baud,
                TxDelay = cfg.TxDelay,
                TxTail = cfg.TxTail,
                HalfDuplex = cfg.HalfDuplex,
                Turnaround = cfg.Turnaround,
                LossRate = cfg.Loss,
                Seed = cfg.Seed,
                TimeScale = cfg.TimeScale,
            }),
            "axudp" => new AxudpChannel(cfg.AxudpPorts.A, cfg.AxudpPorts.B),
            "netsim" => await NetSimChannel.ConnectAsync(
                cfg.NetSim!.Value.A, cfg.NetSim.Value.B, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"unknown channel '{cfg.Channel}'"),
        };

    private static void SendChunked(Ax25Listener listener, Ax25Session session, byte[] payload, int paclen)
    {
        for (var offset = 0; offset < payload.Length; offset += paclen)
        {
            var n = Math.Min(paclen, payload.Length - offset);
            listener.SendData(session, payload.AsMemory(offset, n));
        }
    }

    private static byte[] MakePayload(int bytes, int seed)
    {
        var payload = new byte[bytes];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static TimeSpan Scale(TimeSpan t, double timeScale) =>
        timeScale == 1.0 ? t : TimeSpan.FromTicks((long)(t.Ticks / timeScale));

    private static BenchResult Fail(
        RunConfig cfg, string reason, FrameTap tapA, FrameTap tapB,
        List<AckModeReceipt> receipts, object receiptsGate,
        List<(DateTimeOffset, string, string)> events)
    {
        var (min, mean, max, n) = SummariseReceipts(receipts, receiptsGate);
        return new BenchResult
        {
            Config = cfg,
            Completed = false,
            Failure = reason,
            StatsA = FrameStats.Analyze(tapA.Snapshot(), cfg.EffectiveK, cfg.DupWindow),
            StatsB = FrameStats.Analyze(tapB.Snapshot(), cfg.EffectiveK, cfg.DupWindow),
            AckReceipts = n,
            AckRttMin = min,
            AckRttMean = mean,
            AckRttMax = max,
            TraceA = tapA.Snapshot(),
            TraceB = tapB.Snapshot(),
            Events = events,
        };
    }

    private static (TimeSpan? Min, TimeSpan? Mean, TimeSpan? Max, int Count) SummariseReceipts(
        List<AckModeReceipt> receipts, object gate)
    {
        lock (gate)
        {
            if (receipts.Count == 0) return (null, null, null, 0);
            var elapsed = receipts.Select(r => r.Elapsed).ToList();
            return (
                elapsed.Min(),
                TimeSpan.FromTicks((long)elapsed.Average(e => e.Ticks)),
                elapsed.Max(),
                elapsed.Count);
        }
    }

    /// <summary>Accumulates DL-DATA indications until the expected byte count
    /// lands; <see cref="Done"/> resolves with the arrival time of the last byte.</summary>
    private sealed class ReceiveSink(int expectedBytes)
    {
        private readonly MemoryStream buffer = new();
        private readonly object gate = new();
        private readonly TaskCompletionSource<DateTimeOffset> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DateTimeOffset> Done => expectedBytes == 0 ? Task.FromResult(DateTimeOffset.UtcNow) : done.Task;

        public long BytesReceived
        {
            get { lock (gate) { return buffer.Length; } }
        }

        public void OnSignal(DataLinkSignal sig)
        {
            if (sig is not DataLinkDataIndication data) return;
            lock (gate)
            {
                buffer.Write(data.Info.Span);
                if (buffer.Length >= expectedBytes)
                {
                    done.TrySetResult(DateTimeOffset.UtcNow);
                }
            }
        }

        public bool Matches(byte[] expected)
        {
            lock (gate)
            {
                return buffer.Length == expected.Length &&
                       buffer.GetBuffer().AsSpan(0, (int)buffer.Length).SequenceEqual(expected);
            }
        }

        /// <summary>On an integrity failure, classify HOW the delivered byte stream
        /// diverged from the payload — the channel is drop-only and order-preserving,
        /// so any divergence is the engine delivering to L3 wrongly: extra bytes ⇒
        /// duplicate delivery; equal length but mismatched ⇒ out-of-order delivery;
        /// short ⇒ a gap never filled.</summary>
        public string Diagnose(byte[] expected)
        {
            lock (gate)
            {
                var got = (int)buffer.Length;
                var b = buffer.GetBuffer();
                var firstDiff = -1;
                var n = Math.Min(got, expected.Length);
                for (var i = 0; i < n; i++)
                {
                    if (b[i] != expected[i]) { firstDiff = i; break; }
                }
                var lenNote = got > expected.Length ? $"+{got - expected.Length} EXTRA bytes (duplicate delivery)"
                            : got < expected.Length ? $"-{expected.Length - got} SHORT (gap never filled)"
                            : "exact length";
                var diffNote = firstDiff < 0 ? "no content diff in overlap" : $"first content diff at byte {firstDiff}/{expected.Length}";
                return $"got {got}/{expected.Length} — {lenNote}; {diffNote}";
            }
        }
    }
}
