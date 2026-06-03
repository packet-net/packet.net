using System.Collections.Concurrent;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Xunit;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// AX.25 session-level hardware-loop tests across two USB-attached
/// NinoTNCs whose audio paths are cross-wired. Drives a full
/// connect / 10 240-byte transfer / disconnect through two
/// <see cref="Ax25Session"/> instances — one per TNC — against real
/// wall-clock time. Phase 2 exit criterion: *"Hardware loop sustains
/// 10 kB transfer across NinoTNCs with 0–30 % scripted loss."* per
/// <c>docs/plan.md</c> §5.2 (and issue #213).
/// </summary>
/// <remarks>
/// <para>
/// The two NinoTNCs are expected to be set to MODE DIP=1111 ("Set
/// from KISS") so the test can pick the mode at runtime via SETHW,
/// and to have their TX-DELAY pots at zero so the KISS TXDELAY
/// parameter takes effect. The mode and TXDELAY are applied with the
/// <c>+16</c> non-persist offset (the driver default) so iterating
/// the test matrix doesn't burn the TNC's flash. The analogue SIGNALS
/// DIP block must be configured for the loopback profile documented
/// in the NinoTNC manual (see
/// <a href="https://wiki.oarc.uk/packet:ninotnc">oarc.uk/packet:ninotnc</a>) —
/// the default field-radio profile decodes unreliably on the bench
/// audio cross-wire because the receiver expects a discriminator-
/// shaped signal that the partner TNC's TX isn't producing.
/// </para>
/// <para>
/// The cross-wired audio path emulates a half-duplex FM link with no
/// radios involved — no RF, no antenna. Scripted loss is injected by
/// <see cref="LossyHardwareSender"/> at the TX side of each session
/// so the partner TNC never hears the dropped frame — identical in
/// shape to an RF channel where the frame was corrupted into nothing.
/// </para>
/// <para>
/// <b>The bench audio path is not perfectly lossless</b> — occasional
/// frame-level dropouts happen even with no scripted loss, especially
/// for the slower modes whose longer airtime increases per-frame
/// exposure. Each wire dropout hits the same upstream SDL gaps as
/// the scripted-loss path — both
/// <a href="https://github.com/m0lte/ax25sdl/issues/44">ax25sdl#44</a>
/// (<c>Invoke_Retransmission</c> single-iteration) and
/// <a href="https://github.com/m0lte/ax25sdl/issues/43">ax25sdl#43</a>
/// (<c>Enquiry_Response</c> doesn't set <c>F := 1</c>) — so the
/// no-loss matrix is best-effort: each entry passes in the normal
/// case, but a wire dropout during the ~60 s transfer can stall the
/// link in a perpetual RR-poll cycle. Re-runs typically pass. The
/// flakiness is a real-bench reality combined with known SDL gaps,
/// not a stack regression; both will resolve when those upstream
/// fixes ship.
/// </para>
/// <para>
/// The Segmenter (PR #210) isn't yet wired into <see cref="Ax25Session"/>'s
/// send path, so the 10 240-byte target is hit as 40 × 256-byte
/// DL-DATA-requests. K-window (default 4) throttles outbound to the
/// wire's pace via RR acks; <see cref="Ax25Session.DrainIFrameQueue"/>
/// pops queued frames as V(a) advances. The transfer completes when
/// the partner has seen 40 DL-DATA-indications totalling 10 240 bytes.
/// </para>
/// </remarks>
[Trait("Category", "HardwareLoop")]
[Collection(HardwareLoopCollection.Name)]
public class HardwareLoop10KBTransfer
{
    private const int    PayloadBytes      = 10_240;
    private const int    SegmentSize       = 256;
    private const int    SegmentCount      = PayloadBytes / SegmentSize;  // 40

    // ─── Theories ──────────────────────────────────────────────────────

    /// <summary>
    /// Clean (no scripted loss) 10 kB transfer across a representative
    /// matrix of NinoTNC modes × TXDELAY values. Each mode is an
    /// AX.25-native catalog entry — 9600 GFSK (mode 0) and 1200 AFSK
    /// (mode 6) cover the fast and slow ends of the host's KISS frame
    /// path. TXDELAY values span 50 ms – 400 ms so the audio-only loop
    /// is exercised across the inter-frame pacing the production CSMA /
    /// TX-keying path uses on real radios. The 1200 AFSK row starts at
    /// 150 ms because tighter TXDELAYs cause back-to-back modem-receive
    /// dropouts on the bench cross-wire (the modem needs longer to
    /// re-lock between successive transmissions at that bit rate).
    /// </summary>
    [SkippableTheory]
    [InlineData((byte)0, (byte)5)]    //  9600 GFSK AX.25, TXDELAY  50 ms
    [InlineData((byte)0, (byte)15)]   //  9600 GFSK AX.25, TXDELAY 150 ms
    [InlineData((byte)0, (byte)40)]   //  9600 GFSK AX.25, TXDELAY 400 ms
    [InlineData((byte)6, (byte)15)]   //  1200 AFSK AX.25, TXDELAY 150 ms
    [InlineData((byte)6, (byte)25)]   //  1200 AFSK AX.25, TXDELAY 250 ms
    [InlineData((byte)6, (byte)40)]   //  1200 AFSK AX.25, TXDELAY 400 ms
    public Task Ten_KB_Transfer_Across_Hardware_NinoTNC_Pair(byte modeId, byte txDelayTenMsUnits)
        => RunTransferAsync(modeId, txDelayTenMsUnits, lossProbability: 0.0, srejEnabled: false);

    /// <summary>
    /// 10 kB go-back-N transfer survives scripted loss in each direction. Loss
    /// is an independent per-transmission Bernoulli draw in each direction (a
    /// real-channel model, not a replayed seed — see
    /// <see cref="LossyHardwareSender"/> for why determinism is avoided).
    /// Assertion shape: the transfer completes within a recovery-aware budget;
    /// the lossy sender records non-zero drops; the session reaches
    /// Disconnected cleanly; no FRMR fires; retransmits are observable in the
    /// frame trace (REJ or duplicate I-frames against the same N(s)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On-air rows are go-back-N at 5 % and 15 %, completing via REJ +
    /// Timer-Recovery retransmission. The recovery path was blocked by
    /// ax25sdl#43 (<c>Enquiry_Response</c> F:=1), ax25sdl#44
    /// (<c>Invoke_Retransmission</c> loop), the runtime retransmit-N(s)
    /// renumbering bug, and ax25sdl#53 (recovery-complete <c>vs_eq_nr</c>
    /// guard) — all cleared (Packet.Ax25.Sdl 0.7.1 + runtime fixes).
    /// </para>
    /// <para>
    /// <b>SREJ on-air rows are deferred</b> (the <c>srejEnabled = true</c>
    /// plumbing is in place, and SREJ recovery is proven in-process by
    /// <c>DataLinkConnectedRetransmitTests.SREJ_Recovery_Delivers_Whole_Window_When_Head_Frame_Is_Lost</c>).
    /// On-air at 30 % the link reaches the SREJ × Timer-Recovery path, which
    /// hits a runtime bug — <c>push_frame_on_queue</c> throws on a non-DL-DATA
    /// trigger, killing the session's T1 mid-transition (#225). Re-enable the
    /// SREJ / 30 % rows once that is fixed.
    /// </para>
    /// <para>
    /// Tracker: <see href="https://github.com/m0lte/packet.net/issues/214"/>.
    /// </para>
    /// </remarks>
    [SkippableTheory]
    [InlineData((byte)0, (byte)15, 0.05, false)]   //  9600 GFSK, go-back-N
    [InlineData((byte)0, (byte)15, 0.15, false)]   //  9600 GFSK, go-back-N
    // SREJ / 30 % rows deferred pending the SREJ × Timer-Recovery fix (#214):
    // [InlineData((byte)0, (byte)15, 0.15, true)]    //  9600 GFSK, SREJ
    // [InlineData((byte)0, (byte)15, 0.30, true)]    //  9600 GFSK, SREJ
    // [InlineData((byte)6, (byte)15, 0.30, true)]    //  1200 AFSK, SREJ
    public Task Ten_KB_Transfer_Survives_Scripted_Loss(byte modeId, byte txDelayTenMsUnits, double lossProbability, bool srejEnabled)
        => RunTransferAsync(modeId, txDelayTenMsUnits, lossProbability, srejEnabled);

    // ─── Driver ───────────────────────────────────────────────────────

    private static async Task RunTransferAsync(byte modeId, byte txDelayTenMsUnits, double lossProbability, bool srejEnabled)
    {
        var ports = SelectTwoPorts();
        var mode  = NinoTncCatalog.ByMode[modeId];
        var transferBudget  = ComputeTransferBudget(mode, lossProbability);
        var handshakeBudget = ComputeHandshakeBudget(lossProbability);
        var totalBudget     = handshakeBudget + transferBudget + handshakeBudget + TimeSpan.FromSeconds(20);

        using var cts = new CancellationTokenSource(totalBudget);

        await using var portA = NinoTncSerialPort.Open(ports[0]);
        await using var portB = NinoTncSerialPort.Open(ports[1]);

        await PrepareTncAsync(portA, modeId, txDelayTenMsUnits, cts.Token);
        await PrepareTncAsync(portB, modeId, txDelayTenMsUnits, cts.Token);

        // Mode and TXDELAY take a beat to settle in the NinoTNC after
        // SETHW + KISS TXDELAY. A static wait alone isn't enough for
        // a sustained back-to-back I-frame stream — observed empirically
        // that the first window of session-level I-frames after a mode
        // switch decodes unreliably on the bench cross-wire. Prime each
        // modem with a handful of UI frames in both directions so the
        // TX modulator and RX demodulator actually exercise the path
        // (carrier lock, AGC settle, FCS-validated round-trips) before
        // the session-level transfer hits the wire.
        var aCall = new Callsign("PNLPA", 1);
        var bCall = new Callsign("PNLPB", 2);
        await PrimeModemPathAsync(portA, portB, aCall, bCall, cts.Token);

        // Each side draws loss independently (real-channel Bernoulli, not a
        // replayed seeded sequence) — see LossyHardwareSender for why the seed
        // was removed (deterministic loss × deterministic protocol livelocks
        // at high loss, #214).
        var senderA = new LossyHardwareSender(portA, lossProbability);
        var senderB = new LossyHardwareSender(portB, lossProbability);

        var trace = new ConcurrentQueue<TraceEntry>();
        var rigA = BuildRig("A", aCall, bCall, senderA, trace, srejEnabled);
        var rigB = BuildRig("B", bCall, aCall, senderB, trace, srejEnabled);

        // Inbound pump: classify each AX.25 frame the partner TNC
        // delivers and post it into our session. Address-filter so we
        // ignore anything not addressed to our local callsign — the
        // audio cross-wire is one-way (A-TX → B-RX, B-TX → A-RX) so
        // there's no self-echo to worry about, but the filter keeps
        // the code symmetric with Ax25Listener's pump.
        portA.InboundEvent += (_, evt) => PumpInbound(evt, rigA, "A", trace);
        portB.InboundEvent += (_, evt) => PumpInbound(evt, rigB, "B", trace);

        // ─── Connect ────────────────────────────────────────────────
        SafePost(rigA, new DlConnectRequest());
        var connectConfirm = await WaitForSignalAsync<DataLinkConnectConfirm>(rigA.Signals, handshakeBudget, cts.Token);
        connectConfirm.Should().NotBeNull(
            $"node A must observe UA(F=1) from node B and emit DL-CONNECT-confirm " +
            $"(mode={mode.Name}, txDelay={txDelayTenMsUnits * 10}ms, loss={lossProbability:P0})");
        rigA.Session.CurrentState.Should().Be("Connected");
        await WaitUntilAsync(() => rigB.Session.CurrentState == "Connected",
            TimeSpan.FromSeconds(5), cts.Token);

        // ─── Transfer ───────────────────────────────────────────────
        // Drain any signals queued during connect on B (e.g. the
        // DL-CONNECT-indication B emitted when SABM arrived) so the
        // per-segment DL-DATA-indication assertions aren't confused
        // by a leftover signal.
        while (rigB.Signals.TryDequeue(out _)) { }

        var receivedSegments = new List<byte[]>(SegmentCount);
        var allBytesReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rigB.Session.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is not DataLinkDataIndication ind) return;
            lock (receivedSegments)
            {
                receivedSegments.Add(ind.Info.ToArray());
                if (receivedSegments.Sum(s => s.Length) >= PayloadBytes)
                {
                    allBytesReceived.TrySetResult(true);
                }
            }
        };

        var segments = BuildSegments();
        foreach (var seg in segments)
        {
            SafePost(rigA, new DlDataRequest(seg, Ax25Frame.PidNoLayer3));
        }

        // Wait for the partner to surface every byte we sent.
        var completed = await Task.WhenAny(
            allBytesReceived.Task,
            Task.Delay(transferBudget, cts.Token)).ConfigureAwait(false);
        if (completed != allBytesReceived.Task)
        {
            int gotBytes;
            int gotSegs;
            lock (receivedSegments)
            {
                gotSegs = receivedSegments.Count;
                gotBytes = receivedSegments.Sum(s => s.Length);
            }
            throw new TimeoutException(
                $"transfer did not complete within {transferBudget.TotalSeconds:F0}s. " +
                $"mode={mode.Name} txDelay={txDelayTenMsUnits * 10}ms loss={lossProbability:P0}. " +
                $"Received {gotSegs}/{SegmentCount} segments ({gotBytes}/{PayloadBytes} bytes). " +
                $"A sender: sent={senderA.SentCount} dropped={senderA.DroppedCount}; " +
                $"B sender: sent={senderB.SentCount} dropped={senderB.DroppedCount}. " +
                $"Trace ({trace.Count} entries): {TraceTail(trace, 40)}");
        }

        // Assert reception integrity — every byte arrived and in order.
        byte[] reassembled;
        lock (receivedSegments)
        {
            reassembled = receivedSegments.SelectMany(s => s).ToArray();
        }
        reassembled.Length.Should().Be(PayloadBytes,
            $"every DL-DATA-request byte must surface as a DL-DATA-indication byte on the partner");
        var sent = segments.SelectMany(s => s.ToArray()).ToArray();
        reassembled.Should().Equal(sent, "payload must round-trip intact across the modem pair");

        // ─── Lossy-only assertions ─────────────────────────────────
        if (lossProbability > 0.0)
        {
            (senderA.DroppedCount + senderB.DroppedCount).Should().BeGreaterThan(0,
                "scripted loss should have eliminated at least one outbound frame");
            trace.Should().NotContain(t => t.Kind == TraceKind.Frmr,
                "FRMR must never fire under recoverable loss — it indicates an unrecoverable protocol error");
            var recovery = trace.Count(t => t.Kind is TraceKind.Rej or TraceKind.Srej or TraceKind.IFrameRetransmit);
            recovery.Should().BeGreaterThan(0,
                "retransmits / REJ / SREJ should appear in the trace under scripted loss");
        }

        // ─── Disconnect ─────────────────────────────────────────────
        SafePost(rigA, new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignalAsync<DataLinkDisconnectConfirm>(
            rigA.Signals, handshakeBudget, cts.Token);
        disconnectConfirm.Should().NotBeNull(
            "node A must emit DL-DISCONNECT-confirm after a clean DISC/UA exchange");
        rigA.Session.CurrentState.Should().Be("Disconnected");
        await WaitUntilAsync(() => rigB.Session.CurrentState == "Disconnected",
            TimeSpan.FromSeconds(10), cts.Token);
    }

    // ─── Rig ──────────────────────────────────────────────────────────

    private sealed record Rig(
        string Label,
        Ax25Session Session,
        SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals,
        object PostGate);

    private static Rig BuildRig(
        string label,
        Callsign local,
        Callsign remote,
        LossyHardwareSender sender,
        ConcurrentQueue<TraceEntry> trace,
        bool srejEnabled)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var ctx       = new Ax25SessionContext
        {
            Local = local,
            Remote = remote,
            // Start with a generous initial T1V — the SRT IIR converges
            // T1V toward observed RTT, but the SDL only updates SRT on
            // successful ack windows. With K=4 and a fast hardware loop
            // it takes a few cycles to settle; an overly small initial
            // T1V triggers a TimerRecovery cycle on the *first* K-group.
            T1V = TimeSpan.FromSeconds(8),
            Srt = TimeSpan.FromSeconds(4),
            // More retries: scripted loss makes the default N2=10 too
            // close to the floor for a 40-segment transfer under 30%
            // loss in each direction.
            N2 = 20,
            // Selective reject: the modems are dumb KISS, so SREJ lives
            // entirely in our session layer — forcing the flag (rather than
            // negotiating via XID) is sufficient. With SREJ the receiver
            // keeps post-gap frames instead of discarding the whole window
            // on a lost head, which is what lets it survive heavier loss.
            SrejEnabled = srejEnabled,
        };
        var signals   = new ConcurrentQueue<DataLinkSignal>();

        // Track the most-recent N(s) observed in outbound I-frames so
        // we can flag actual retransmits (same N(s) within a half-
        // modulus window) without false-positiving on the mod-8 wrap
        // of fresh frames. A retransmit always re-sends a *recent*
        // N(s); a new frame's N(s) advances forward.
        byte? lastSentNs = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            if (Ax25Frame.TryParse(bytes.Span, out var parsed))
            {
                var kind = ClassifyForTrace(parsed);
                if (kind == TraceKind.IFrame)
                {
                    byte ns = (byte)((parsed.Control >> 1) & 0x07);
                    if (lastSentNs is byte prev)
                    {
                        // forward distance from previous N(s); 0 = same
                        // frame (definite retransmit); 1..3 = normal
                        // progress within a K=4 window; 4..7 = backward
                        // (i.e. retransmit, modulo 8).
                        int forward = (ns - prev + 8) % 8;
                        if (forward == 0 || forward >= 4)
                        {
                            kind = TraceKind.IFrameRetransmit;
                        }
                    }
                    lastSentNs = ns;
                }
                trace.Enqueue(new TraceEntry(DateTimeOffset.UtcNow, label, "TX", kind, parsed.Control));
            }
            _ = sender.SendFrameAsync(bytes);
        }

        Ax25Session? sessionRef = null;
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => SafePost(sessionRef!, ToTimerEvent(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    sig =>
            {
                signals.Enqueue(sig);
                sessionRef?.RaiseDataLinkSignal(sig);
            },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines)
        {
            // Fast ack delay: a 256-byte I-frame at 9600 baud rides the
            // wire in ~225 ms; default T2 = 1500 ms is way too long
            // and causes A's T1 (which shrinks toward the observed RTT
            // via figc4.7's SRT IIR) to expire before B's piggyback ack
            // arrives — manifests as a RR-poll cycle between every
            // K-window. 200 ms keeps T2 well under any steady-state T1
            // value we'd reasonably converge to on a back-to-back link.
            T2Duration = TimeSpan.FromMilliseconds(200),
            // Long-idle timer: connect / 40-segment transfer / disconnect
            // can easily exceed 30 s under loss + slow modes. Bump high
            // enough that T3 only fires if the link is genuinely stuck.
            T3Duration = TimeSpan.FromMinutes(5),
        };

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards   = new GuardEvaluator(bindings);
        var session  = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;

        return new Rig(label, session, scheduler, signals, new object());
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Per-session PostEvent serialiser. The inbound pump (driven by
    /// <see cref="NinoTncSerialPort.InboundEvent"/> on the read-pump
    /// thread) and the test thread (firing DL-DATA-requests / DL-CONNECT
    /// / DL-DISCONNECT) both call into the same <see cref="Ax25Session"/>.
    /// PostEvent mutates context state without locking, so we serialise
    /// at the call site.
    /// </summary>
    private static void SafePost(Rig rig, Ax25Event evt)
    {
        lock (rig.PostGate) rig.Session.PostEvent(evt);
    }

    /// <summary>
    /// Overload used by the dispatcher's timer-expiry callback, which
    /// captures the session before the rig record exists.
    /// </summary>
    private static void SafePost(Ax25Session session, Ax25Event evt)
    {
        // Session-local lock — the dispatcher closure doesn't have a
        // handle to the rig, so we lock on the session itself. The
        // session is the same object the rig's PostGate is gating, so
        // a single per-rig lock would be cleaner; this fallback path
        // is rare (only fires on timer expiry) and serialises against
        // itself, which is sufficient.
        lock (session) session.PostEvent(evt);
    }

    private static Ax25Event ToTimerEvent(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };

    private static void PumpInbound(KissInboundEvent evt, Rig rig, string label, ConcurrentQueue<TraceEntry> trace)
    {
        if (evt is not Ax25FrameReceivedEvent ax25) return;
        var frame = ax25.Ax25;
        if (!frame.Destination.Callsign.Equals(rig.Session.Context.Local)) return;

        trace.Enqueue(new TraceEntry(DateTimeOffset.UtcNow, label, "RX", ClassifyForTrace(frame), frame.Control));
        SafePost(rig, Ax25FrameClassifier.Classify(frame));
    }

    private static async Task<T?> WaitForSignalAsync<T>(
        ConcurrentQueue<DataLinkSignal> signals,
        TimeSpan budget,
        CancellationToken outer) where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            while (signals.TryDequeue(out var sig))
            {
                if (sig is T match) return match;
            }
            try { await Task.Delay(25, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(25, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Budget for the connect (SABM/UA) and disconnect (DISC/UA) handshakes.
    /// A handshake is one round trip, but under loss each direction's frame can
    /// drop, so it takes several T1-driven retries. With per-direction loss p
    /// the round-trip success rate is (1-p)², so the base 30 s is inflated by
    /// the odds of loss to keep the handshake from timing out on an unlucky
    /// run (e.g. ~80 s at 30 %). Loss is now uncorrelated per transmission, so
    /// a handshake that stalls escapes on the next retry — given enough budget.
    /// </summary>
    private static TimeSpan ComputeHandshakeBudget(double lossProbability)
    {
        double baseSeconds = 30.0;
        if (lossProbability <= 0.0) return TimeSpan.FromSeconds(baseSeconds);
        double recovery = lossProbability / (1.0 - lossProbability) * 120.0;
        return TimeSpan.FromSeconds(baseSeconds + recovery);
    }

    private static TimeSpan ComputeTransferBudget(NinoTncMode mode, double lossProbability)
    {
        // Walltime-equivalent of the raw payload at the mode's bit rate,
        // padded for I-frame overhead (addr + ctrl + pid + FCS ≈ 18 bytes
        // per 256-byte segment plus ack RR frames in the other direction),
        // TXDELAY pacing, and the K-window stop-and-wait gaps.
        double airTimeSeconds = mode.BitRateHz > 0
            ? (PayloadBytes + SegmentCount * 32.0) * 8.0 / mode.BitRateHz
            : 60.0;
        double overheadMultiplier = 4.0;     // K-window stop-and-wait + TXDELAY + RR turnaround
        double airtimeBudget = airTimeSeconds * overheadMultiplier;

        if (lossProbability <= 0.0)
        {
            return TimeSpan.FromSeconds(Math.Max(90.0, airtimeBudget));
        }

        // Under scripted loss the wall-clock is dominated NOT by extra airtime
        // but by timeout-driven recovery: a dropped frame whose REJ also can't
        // get back (or a dropped poll / poll-response) falls back to a full T1V
        // wait before the next poll. Each such cycle costs ~T1V, and the count
        // grows with the per-direction loss rate. Calibrated against a measured
        // bench run — 15 % loss on 9600 GFSK completed in ~205 s (≈ 5 s/segment,
        // T1V = 8 s): the recovery term below yields ~377 s there (≈1.8× headroom
        // for run-to-run bench variance) and scales up with p and slower modes.
        const double T1VSeconds = 8.0;       // matches the session's initial T1V
        double recoveryBudget = SegmentCount
            * (lossProbability / (1.0 - lossProbability))
            * T1VSeconds * 6.0;
        double budget = airtimeBudget + recoveryBudget;
        return TimeSpan.FromSeconds(Math.Max(120.0, budget));
    }

    private static List<ReadOnlyMemory<byte>> BuildSegments()
    {
        var segments = new List<ReadOnlyMemory<byte>>(SegmentCount);
        for (int i = 0; i < SegmentCount; i++)
        {
            var buf = new byte[SegmentSize];
            for (int j = 0; j < SegmentSize; j++)
            {
                // Deterministic pattern keyed by global byte offset so
                // any reordering or truncation shows up in the assert.
                int offset = i * SegmentSize + j;
                buf[j] = (byte)((offset * 31 + i) & 0xFF);
            }
            segments.Add(buf);
        }
        return segments;
    }

    private static List<string> SelectTwoPorts()
    {
        var candidates = NinoTncPortDiscovery.EnumerateCandidates();
        Skip.If(
            candidates.Count < 2,
            $"Hardware-loop test: expected ≥2 NinoTNC-class serial devices, " +
            $"found {candidates.Count}. Connect both TNCs over USB and re-run, " +
            $"or set {NinoTncPortDiscovery.PortsEnvVar}=\"<porta>,<portb>\" to pick explicitly.");
        return candidates.Take(2).Select(c => c.PortName).ToList();
    }

    private static async Task PrepareTncAsync(NinoTncSerialPort tnc, byte modeId, byte txDelayTenMsUnits, CancellationToken ct)
    {
        // persistToFlash=false applies the +16 non-persist offset so the
        // mode lives only for this power cycle — repeated theory
        // invocations don't burn flash.
        await tnc.SetModeAsync(modeId, persistToFlash: false, ct).ConfigureAwait(false);
        await tnc.SetTxDelayAsync(txDelayTenMsUnits, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run a brief UI-frame priming round-trip across the modem pair
    /// after mode + TXDELAY are configured. Sends <c>PrimingFrames</c>
    /// UI frames from A→B and again from B→A, waiting briefly for
    /// each direction to be observed on the partner before continuing.
    /// </summary>
    /// <remarks>
    /// A static <c>Task.Delay</c> after SETHW lets the firmware settle
    /// the mode register but does <em>not</em> exercise the analogue
    /// TX/RX path — the first session-level I-frame still hits a cold
    /// modulator/demodulator. Priming with real frames forces a few
    /// FCS-validated round-trips up front so the carrier-lock, AGC,
    /// and clock-recovery loops are already at steady state when the
    /// session-level transfer starts. Failures here are tolerated
    /// (best-effort): if priming frames don't arrive, the test will
    /// surface it as a session-level failure with the same diagnostic
    /// trace shape.
    /// </remarks>
    private static async Task PrimeModemPathAsync(
        NinoTncSerialPort portA, NinoTncSerialPort portB,
        Callsign aCall, Callsign bCall, CancellationToken ct)
    {
        await PrimeOneDirectionAsync(portA, portB, aCall, bCall, "A->B", ct).ConfigureAwait(false);
        await PrimeOneDirectionAsync(portB, portA, bCall, aCall, "B->A", ct).ConfigureAwait(false);
    }

    private const int PrimingFrames = 3;

    private static async Task PrimeOneDirectionAsync(
        NinoTncSerialPort tx, NinoTncSerialPort rx,
        Callsign src, Callsign dst, string label, CancellationToken ct)
    {
        int observed = 0;
        void OnRx(object? _, KissInboundEvent evt)
        {
            if (evt is Ax25FrameReceivedEvent ax25
                && ax25.Ax25.Source.Callsign.Equals(src)
                && ax25.Ax25.Destination.Callsign.Equals(dst))
            {
                Interlocked.Increment(ref observed);
            }
        }
        rx.InboundEvent += OnRx;
        try
        {
            for (int i = 0; i < PrimingFrames; i++)
            {
                var frame = Ax25Frame.Ui(
                    destination: dst,
                    source: src,
                    info: System.Text.Encoding.ASCII.GetBytes($"PRIME-{label}-{i}"));
                await tx.SendFrameAsync(frame.ToBytes(), ct).ConfigureAwait(false);
                // Brief inter-frame gap so the partner's demodulator
                // can finish the previous frame before the next one
                // hits the wire. The exact value isn't load-bearing —
                // anything in the 100–500 ms range gives the modem
                // time to drop carrier and re-acquire.
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            // Final settle so any in-flight inbound frame finishes
            // dispatching before we move to the session transfer.
            var settleDeadline = DateTime.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref observed) < PrimingFrames && DateTime.UtcNow < settleDeadline)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            rx.InboundEvent -= OnRx;
        }
    }

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]          = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"]             = DataLink_Connected.Transitions,
        ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]         = DataLink_TimerRecovery.Transitions,
    };

    // ─── Trace ────────────────────────────────────────────────────────

    private enum TraceKind
    {
        Sabm, Sabme, Disc, Ua, Dm, Frmr, Xid, Test, Ui,
        IFrame, IFrameRetransmit,
        Rr, Rnr, Rej, Srej,
        Other,
    }

    private sealed record TraceEntry(DateTimeOffset At, string Label, string Direction, TraceKind Kind, byte Control);

    private static TraceKind ClassifyForTrace(Ax25Frame frame)
    {
        byte ctrl = frame.Control;
        if ((ctrl & 0x01) == 0) return TraceKind.IFrame;
        if ((ctrl & 0x03) == 0x01)
        {
            return (ctrl & 0x0C) switch
            {
                0x00 => TraceKind.Rr,
                0x04 => TraceKind.Rnr,
                0x08 => TraceKind.Rej,
                0x0C => TraceKind.Srej,
                _    => TraceKind.Other,
            };
        }
        byte uBase = (byte)(ctrl & 0xEF);
        return uBase switch
        {
            0x2F => TraceKind.Sabm,
            0x6F => TraceKind.Sabme,
            0x43 => TraceKind.Disc,
            0x63 => TraceKind.Ua,
            0x0F => TraceKind.Dm,
            0x87 => TraceKind.Frmr,
            0xAF => TraceKind.Xid,
            0xE3 => TraceKind.Test,
            0x03 => TraceKind.Ui,
            _    => TraceKind.Other,
        };
    }

    private static string TraceTail(ConcurrentQueue<TraceEntry> trace, int max)
    {
        var entries = trace.ToArray();
        var tail = entries.Skip(Math.Max(0, entries.Length - max));
        return string.Join(", ",
            tail.Select(t => $"{t.Label}-{t.Direction}:{t.Kind}(0x{t.Control:X2})"));
    }
}
