using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Ax25.Xid;
using Packet.Core;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Reusable two-station conformance harness: two real <see cref="Ax25Session"/>
/// instances over a controllable in-process <see cref="Channel"/>, a shared
/// <see cref="FakeTimeProvider"/>, and a single-threaded, ordered pump. The
/// substrate for the conformance / generative-testing platform
/// (<c>docs/conformance-harness-plan.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// A run is a pure function of the scenario (the sequence of <see cref="Submit"/>
/// / <see cref="Connect"/> / <see cref="AdvanceT1"/> calls) and the channel
/// policy (<see cref="Channel.Drop"/> and friends) — fully deterministic,
/// reproducible, and replayable. No wall-clock anywhere.
/// </para>
/// <para>
/// The harness tracks what each station <em>submitted</em> (the payloads handed
/// to <c>DL-DATA request</c>) and what it <em>delivered</em> upward
/// (<see cref="DataLinkDataIndication"/>), so <see cref="InvariantChecker"/> can
/// judge reliable in-order delivery end-to-end. Every drive method
/// (<see cref="Settle"/>, <see cref="DrainOnce"/>, <see cref="AdvanceT1"/>,
/// <see cref="Submit"/>, <see cref="Connect"/>) runs the safety invariants after
/// it returns, so a violation is attributed to the step that caused it.
/// </para>
/// </remarks>
public sealed class TwoStationHarness
{
    public const int DefaultT1Ms = 200;
    public const int DefaultN2   = 12;

    public Endpoint A { get; }
    public Endpoint B { get; }
    public Channel Link { get; }
    public FakeTimeProvider Time { get; }
    public TimeSpan T1V { get; }
    public TimeSpan T2 { get; }

    private readonly HashSet<(string From, string Id)> fired = new();

    /// <summary>Every <c>(state, transition-id)</c> that has fired on either
    /// station's real dispatcher over this harness's lifetime — the substrate for
    /// behavioural transition-coverage measurement (see
    /// <c>TransitionCoverageTests</c>). Populated from each session's
    /// <see cref="Ax25Session.TransitionFired"/> event.</summary>
    public IReadOnlyCollection<(string From, string Id)> FiredTransitions => fired;

    /// <summary>When false, the drive methods skip the post-step invariant
    /// check — used by adversarial scenarios that assert on the converged state
    /// only. Defaults true (oracle runs after every step).</summary>
    public bool CheckAfterEachStep { get; set; } = true;

    private TwoStationHarness(Endpoint a, Endpoint b, Channel link, FakeTimeProvider time, TimeSpan t1v, TimeSpan t2)
    {
        A = a; B = b; Link = link; Time = time; T1V = t1v; T2 = t2;
    }

    /// <summary>The station that is not <paramref name="e"/>.</summary>
    public Endpoint Peer(Endpoint e) => ReferenceEquals(e, A) ? B : A;

    // ─── Construction ───────────────────────────────────────────────────

    public static TwoStationHarness Build(
        bool srej = false, int k = 4, int t1Ms = DefaultT1Ms, int n2 = DefaultN2, int t2Ms = 40,
        bool extended = false, Ax25SessionQuirks? quirks = null,
        XidParameters? xidOfferA = null, XidParameters? xidOfferB = null,
        bool segmenter = false, int? n1 = null)
    {
        var q = quirks ?? Ax25SessionQuirks.Default;
        var nodeA = new Callsign("M0LTEA", 1);
        var nodeB = new Callsign("M0LTEB", 2);
        var time  = new FakeTimeProvider();
        var link  = new Channel();
        var t1v   = TimeSpan.FromMilliseconds(t1Ms);

        var a = BuildEndpoint(nodeA, nodeB, time, link, srej, k, t1Ms, n2, t2Ms, extended, q, xidOfferA, segmenter, n1, out var aPeer);
        var b = BuildEndpoint(nodeB, nodeA, time, link, srej, k, t1Ms, n2, t2Ms, extended, q, xidOfferB, segmenter, n1, out var bPeer);
        aPeer.Target = b.Inbound;          bPeer.Target = a.Inbound;
        aPeer.RxLog  = b.ReceivedFromPeer; bPeer.RxLog  = a.ReceivedFromPeer;
        // When A sends, the frame is delivered to B's wiring; route XID/FRMR to
        // B's MDL while it negotiates (and vice-versa). The peer thunk reads the
        // built Endpoint's MDL; MDL deliveries are deferred onto the peer's work
        // queue (drained by the pump).
        aPeer.Mdl = () => b.Mdl;           bPeer.Mdl = () => a.Mdl;
        aPeer.MdlWork = b.MdlWork;         bPeer.MdlWork = a.MdlWork;
        var harness = new TwoStationHarness(a, b, link, time, t1v, TimeSpan.FromMilliseconds(t2Ms));
        a.Session.TransitionFired += (_, spec) => harness.fired.Add((spec.From, spec.Id));
        b.Session.TransitionFired += (_, spec) => harness.fired.Add((spec.From, spec.Id));
        // The MDL (management_data_link) machine runs its own Ax25Session; its
        // Ready/Negotiating state names don't collide with the data-link states,
        // so the same (From, Id) ledger captures its transitions too. This lets
        // the coverage ledger track the MDL machine (V5a).
        a.Mdl.TransitionFired += (_, spec) => harness.fired.Add((spec.From, spec.Id));
        b.Mdl.TransitionFired += (_, spec) => harness.fired.Add((spec.From, spec.Id));
        return harness;
    }

    /// <summary>Build a harness whose sessions run the SDL figures exactly as
    /// drawn — every <see cref="Ax25SessionQuirks"/> off. Used to pin a figure
    /// defect's faithful (uncorrected) behaviour alongside the corrected default.</summary>
    public static TwoStationHarness BuildStrictlyFaithful(
        bool srej = false, int k = 4, int t1Ms = DefaultT1Ms, int n2 = DefaultN2, int t2Ms = 40,
        bool extended = false)
        => Build(srej, k, t1Ms, n2, t2Ms, extended, Ax25SessionQuirks.StrictlyFaithful);

    private static Endpoint BuildEndpoint(
        Callsign local, Callsign remote, FakeTimeProvider time, Channel link,
        bool srej, int k, int t1Ms, int n2, int t2Ms, bool extended, Ax25SessionQuirks quirks,
        XidParameters? xidOffer, bool segmenter, int? n1, out PeerWiring peer)
    {
        peer = new PeerWiring { TargetLocal = remote };
        var peerLocal = peer;
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local = local, Remote = remote,
            Srt = TimeSpan.FromMilliseconds(t1Ms / 2),
            T1V = TimeSpan.FromMilliseconds(t1Ms),
            N2  = n2,
            K   = k,
            SrejEnabled = srej,
            IsExtended  = extended,
            Quirks      = quirks,
            SegmenterReassemblerEnabled = segmenter,
        };
        if (n1 is { } n1Value) ctx.N1 = n1Value;
        var segmentation = new SegmentationLayer(ctx);
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var mdlSignals = new ConcurrentQueue<MdlSignal>();
        var inbound = new Queue<Ax25Event>();
        var rxLog = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            // Parse at the link's modulo. The harness is symmetric (both
            // endpoints share `extended`), and an I/S frame only ever flows once
            // both sides agree on the modulo, so the sender's modulo (ctx) equals
            // the receiver's. U frames are 1 octet in both modes regardless.
            if (!Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, ctx.IsExtended, out var parsed)) return;
            if (link.ShouldDrop(parsed)) return;
            if (peerLocal.TargetLocal is { } expected && !parsed.Destination.Callsign.Equals(expected)) return;
            DeliverToPeer(parsed);
            if (link.ShouldDuplicate(parsed)) DeliverToPeer(parsed);

            void DeliverToPeer(Ax25Frame frame)
            {
                peerLocal.RxLog?.Add(frame);
                var evt = Ax25FrameClassifier.Classify(frame);
                // Mirror the listener's MDL routing (see Ax25Listener.DispatchInbound):
                //   XID command           → responder builds the XID response
                //   XID response (negotiating) → initiator applies negotiated params
                //   FRMR (negotiating)    → initiator v2.0 fallback
                //
                // MDL deliveries are DEFERRED onto the peer's work queue rather
                // than invoked synchronously: in production frames go out the
                // modem and return through the async inbound pump, so the sender's
                // own MDL transition completes before the reply is processed.
                // Invoking synchronously here would re-enter the sender's MDL
                // PostEvent mid-transition (XID command not yet committed → still
                // in Ready), mis-routing the reply. The pump drains MdlWork.
                var peerMdl = peerLocal.Mdl?.Invoke();
                if (peerMdl is not null && evt is XidReceived && frame.IsCommand)
                {
                    peerLocal.MdlWork?.Enqueue(() => peerMdl.RespondToXidCommand(frame));
                    return;
                }
                if (peerMdl is { IsNegotiating: true } && evt is XidReceived)
                {
                    peerLocal.MdlWork?.Enqueue(() => peerMdl.OnXidReceived(frame));
                    return;
                }
                if (peerMdl is { IsNegotiating: true } && evt is FrmrReceived)
                {
                    peerLocal.MdlWork?.Enqueue(() => peerMdl.OnFrmrReceived(frame));
                    return;
                }
                peerLocal.Target?.Enqueue(evt);
            }
        }

        // The MDL driver shares this endpoint's scheduler + wire sink. Built
        // before the data-link dispatcher so sendInternal can route the
        // MDL-NEGOTIATE-request poke (raised by figc4.6 after the UA on a v2.2
        // connect) straight into it. Negotiated parameters mutate ctx — the same
        // context the data-link session runs on — which is the whole point.
        var mdl = new Ax25ManagementDataLink(ctx, scheduler, SendBytes, offered: xidOffer);
        mdl.MdlSignalEmitted += (_, sig) => mdlSignals.Enqueue(sig);

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            // Receive-side segmentation seam — mirrors Ax25Listener.SendUpward:
            // a 0x08-PID DL-DATA indication is fed to the reassembler and only
            // surfaced (as one reassembled indication) when the series completes;
            // a non-segment indication passes through unchanged; non-DATA signals
            // bypass the shim. So Endpoint.Delivered shows reassembled payloads,
            // letting the convergence oracle compare one logical submission to one
            // logical delivery.
            sendUpward:  sig =>
            {
                if (sig is DataLinkDataIndication dataInd)
                {
                    var reassembled = segmentation.OnDataIndication(dataInd);
                    if (reassembled is null) return;
                    sig = reassembled;
                }
                signals.Enqueue(sig);
            },
            // Model a contention-free medium: grant LM-SEIZE immediately so the
            // figc4.4 delayed-ack (Set Ack Pending + LM-SEIZE Request → flush the
            // RR on LM-SEIZE-confirm) actually flushes. Without this the link can
            // only ack via T1 polls — which is why the legacy rigs (all of which
            // stub sendLinkMux) never exercised autonomous delayed-ack.
            sendLinkMux: signal => { if (signal is LinkMultiplexerSeizeRequest) inbound.Enqueue(new LmSeizeConfirm()); },
            sendInternal: sig => { if (sig is MdlNegotiateRequestSignal) mdl.Negotiate(); },
            subroutines: subroutines)
        {
            // T1 uses ctx.T1V; T2/T3 come from these dispatcher properties.
            // Keep T2 (delayed-ack) well below T1V so FlushAcks can flush a
            // piggyback-free RR without tripping the T1 retransmit timer.
            T2Duration = TimeSpan.FromMilliseconds(t2Ms),
        };

        var guards = new GuardEvaluator(
            Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger));
        subroutines.Wire(dispatcher, guards);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards, TransitionMap(), "Disconnected");
        sessionRef = session;
        return new Endpoint(local.ToString(), session, ctx, signals, inbound, rxLog, mdl, mdlSignals, segmentation);
    }

    // ─── Scenario actions ───────────────────────────────────────────────

    /// <summary>Establish the link from A. Asserts both reach Connected, then
    /// drains connect-time signals so delivery tracking starts clean.</summary>
    public void Connect() => ConnectFrom(A);

    /// <summary>Establish the link from <paramref name="initiator"/>.</summary>
    public void ConnectFrom(Endpoint initiator)
    {
        initiator.Session.PostEvent(new DlConnectRequest());
        PumpToQuiescence();
        if (A.State != "Connected" || B.State != "Connected")
            throw new InvariantViolationException($"connect failed: A={A.State} B={B.State}");
        DrainSignals(A); DrainSignals(B);
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Mark <paramref name="e"/> busy (DL-FLOW-OFF) — it sends RNR and
    /// the peer must stop sending I-frames.</summary>
    public void SetBusy(Endpoint e)
    {
        e.Session.PostEvent(new DlFlowOffRequest());
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Clear <paramref name="e"/>'s busy condition (DL-FLOW-ON) — it
    /// sends RR and the peer may resume.</summary>
    public void ClearBusy(Endpoint e)
    {
        e.Session.PostEvent(new DlFlowOnRequest());
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Submit one payload at <paramref name="from"/> for its peer;
    /// records it for the reliable-delivery invariant and posts DL-DATA-request.</summary>
    public void Submit(Endpoint from, params byte[] payload)
    {
        from.Submitted.Add(payload);
        from.Session.PostEvent(new DlDataRequest(payload));
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Submit a back-to-back BURST of one-byte payloads at
    /// <paramref name="from"/> for its peer, settling only once at the end so every
    /// frame is in flight together (V(S) advances across the whole burst before any
    /// ack returns). This is the regime the per-frame <see cref="Submit"/> never
    /// reaches — it pumps to quiescence after each frame, serialising the transfer —
    /// and it is what surfaces the mod-8 SREJ sequence-ring-wrap bug: only when ≥ k
    /// frames fly together does N(S) wrap mid-recovery and a stale retransmit alias
    /// an already-delivered number. Each payload is recorded as its own logical
    /// submission so the convergence oracle expects one delivery per byte.</summary>
    public void SubmitBurst(Endpoint from, params byte[] payloads)
    {
        foreach (var b in payloads)
        {
            from.Submitted.Add(new[] { b });
            from.Session.PostEvent(new DlDataRequest(new[] { b }));
        }
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Submit one (possibly &gt; N1) upper-layer payload through the §6.6
    /// segmentation shim at <paramref name="from"/>, carrying Layer-3 PID
    /// <paramref name="pid"/>. Records the WHOLE payload as a single logical
    /// submission (so the oracle expects one reassembled delivery), then posts each
    /// segment-request the shim produces as its own I-frame. With the segmenter
    /// disabled this posts a single un-segmented request (and throws if the payload
    /// exceeds N1, per the shim's strict reject).</summary>
    public void SubmitLarge(Endpoint from, byte[] payload, byte pid = Ax25Frame.PidNoLayer3)
    {
        from.Submitted.Add(payload);
        foreach (var request in from.Segmentation.BuildSendRequests(payload, pid))
        {
            from.Session.PostEvent(request);
        }
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }


    /// <summary>Disconnect from <paramref name="from"/>; asserts both reach
    /// Disconnected.</summary>
    public void Disconnect(Endpoint from)
    {
        from.Session.PostEvent(new DlDisconnectRequest());
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    // ─── Inbound injection (reaching the "as received" paths) ────────────

    /// <summary>Inject a received-frame event straight into
    /// <paramref name="target"/>'s session, then pump + check. Models a frame
    /// arriving on <paramref name="target"/>'s radio that the *peer session*
    /// would never emit on its own — so it reaches received-frame transitions the
    /// two well-behaved sessions can't drive between them (a FRMR, an unsolicited
    /// DM, a malformed frame). Bypasses the channel's drop/duplicate/address
    /// filters: the frame is, by construction, "already at the receiver".</summary>
    public void Inject(Endpoint target, Ax25Event evt)
    {
        target.Inbound.Enqueue(evt);
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Inject raw frame bytes at <paramref name="target"/>'s receiver:
    /// parse (lenient) + classify through the real <see cref="Ax25FrameClassifier"/>,
    /// then <see cref="Inject(Endpoint, Ax25Event)"/>. Exercises the full inbound
    /// codec path, so a real <see cref="Ax25Frame.Frmr"/>/<see cref="Ax25Frame.Dm"/>
    /// (or a hand-built malformed control byte) becomes the event the dispatcher
    /// actually sees. Build the frame addressed <em>to</em> the target
    /// (<c>destination: target.Context.Local, source: target.Context.Remote</c>)
    /// so it survives any address check the receive path applies.</summary>
    public void InjectFrameBytes(Endpoint target, ReadOnlyMemory<byte> bytes)
    {
        if (!Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, target.Context.IsExtended, out var parsed))
            throw new InvalidOperationException(
                "InjectFrameBytes: the supplied bytes did not parse as an AX.25 frame");
        Inject(target, Ax25FrameClassifier.Classify(parsed));
    }

    /// <summary>Advance the clock past one T1 interval and pump to quiescence —
    /// fires any due timers and lets the resulting cascade settle.</summary>
    public void AdvanceT1(int extraMs = 20)
    {
        // Advance past whichever endpoint's *live* T1V is largest — T1V can grow
        // (figc4.7 SRT backoff), and a fixed advance would stop firing an armed
        // T1 once it grew past it, stalling recovery (and masking real bugs).
        var t1 = A.Context.T1V > B.Context.T1V ? A.Context.T1V : B.Context.T1V;
        Time.Advance(t1 + TimeSpan.FromMilliseconds(extraMs));
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Directly start an MDL XID negotiation from <paramref name="e"/>
    /// (posts the MDL-NEGOTIATE Request the data-link figc4.6 path would raise on
    /// a v2.2 connect), then pump. Lets MDL tests exercise negotiation without
    /// having to also reproduce the full SABME handshake first.</summary>
    public void StartNegotiation(Endpoint e)
    {
        e.Mdl.Negotiate();
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Advance the clock past one TM201 interval (the MDL management
    /// retry timer) and pump — fires a due TM201 retry / give-up and lets the
    /// cascade settle. TM201 defaults to 3000 ms in the MDL driver; advance past
    /// the larger of that and the live T1V so the timer fires regardless of
    /// whether the data-link has (re)set T1V.</summary>
    public void AdvanceTm201(int extraMs = 50)
    {
        var t1 = A.Context.T1V > B.Context.T1V ? A.Context.T1V : B.Context.T1V;
        var floor = TimeSpan.FromMilliseconds(3000);   // ActionDispatcher.Tm201Duration default
        var step = (t1 > floor ? t1 : floor) + TimeSpan.FromMilliseconds(extraMs);
        Time.Advance(step);
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Advance just past the delayed-ack timer T2 (but well short of
    /// T1), so a receiver's pending RR flushes and the sender's V(a) / window
    /// catches up — without provoking a spurious T1 retransmit. Use after a
    /// burst of <see cref="Submit"/> to settle a one-directional transfer's acks.</summary>
    public void FlushAcks()
    {
        Time.Advance(T1V / 2);   // T2 < T1V/2 by construction
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Pump both inbound queues to quiescence (a logical "settle"),
    /// then run the oracle.</summary>
    public void Settle()
    {
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
    }

    /// <summary>Process exactly the events already queued on both endpoints —
    /// one pass, no pump-to-quiescence. Models a single T1-spaced round (used by
    /// the paced recovery scenarios). Frames emitted in response land in the
    /// peer's queue but are not processed until the next drain/advance.</summary>
    public void DrainOnce()
    {
        int an = A.Inbound.Count, bn = B.Inbound.Count;
        for (int i = 0; i < an && A.Inbound.TryDequeue(out var e); i++) A.Session.PostEvent(e);
        for (int i = 0; i < bn && B.Inbound.TryDequeue(out var e); i++) B.Session.PostEvent(e);
        if (CheckAfterEachStep) CheckInvariants();
    }

    // ─── Oracle ─────────────────────────────────────────────────────────

    /// <summary>Run the safety invariants (throws <see cref="InvariantViolationException"/>
    /// on the first failure).</summary>
    public void CheckInvariants() => InvariantChecker.CheckSafety(this);

    /// <summary>Assert the link has fully converged: everything submitted is
    /// delivered in order and both windows are empty (V(s)==V(a)).</summary>
    public void AssertConverged() => InvariantChecker.AssertConverged(this);

    // ─── Pump ───────────────────────────────────────────────────────────

    private void PumpToQuiescence()
    {
        for (int i = 0; i < 256; i++)
        {
            var progress = false;
            while (A.Inbound.TryDequeue(out var evt)) { A.Session.PostEvent(evt); progress = true; }
            while (B.Inbound.TryDequeue(out var evt)) { B.Session.PostEvent(evt); progress = true; }
            // Deferred MDL work (XID command/response/FRMR routed to the MDL
            // machine) — drained after the data-link events so a just-sent XID
            // command's MDL transition has committed before its reply is handled.
            while (A.MdlWork.TryDequeue(out var work)) { work(); progress = true; }
            while (B.MdlWork.TryDequeue(out var work)) { work(); progress = true; }
            if (!progress) return;
        }
        throw new InvariantViolationException("link did not settle within 256 round-trips — possible send/ack livelock");
    }

    private static void DrainSignals(Endpoint e)
    {
        while (e.Signals.TryDequeue(out _)) { }
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

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry '{name}'"),
    };

    // ─── Channel + endpoint types ───────────────────────────────────────

    /// <summary>The in-process medium. Phase H uses deliver-only; the
    /// adversarial phases set <see cref="Drop"/> (and, later, delay / reorder /
    /// duplicate / corrupt policies).</summary>
    public sealed class Channel
    {
        /// <summary>Return true to drop the frame at the link layer (it never
        /// reaches the peer). Null = clean channel.</summary>
        public Func<Ax25Frame, bool>? Drop { get; set; }

        public bool ShouldDrop(Ax25Frame f) => Drop?.Invoke(f) == true;

        /// <summary>Return true to deliver the frame to the peer a second time —
        /// the medium duplicated it (a digipeater echo, or a retransmit arriving
        /// alongside the original it was meant to replace). The receiver must
        /// discard the duplicate and never deliver its payload twice. Null = no
        /// duplication.</summary>
        public Func<Ax25Frame, bool>? Duplicate { get; set; }

        public bool ShouldDuplicate(Ax25Frame f) => Duplicate?.Invoke(f) == true;
    }

    public sealed class Endpoint
    {
        public string Name { get; }
        public Ax25Session Session { get; }
        public Ax25SessionContext Context { get; }
        public ConcurrentQueue<DataLinkSignal> Signals { get; }
        public Queue<Ax25Event> Inbound { get; }
        public List<Ax25Frame> ReceivedFromPeer { get; }

        /// <summary>This station's MDL (XID-negotiation) driver.</summary>
        public Ax25ManagementDataLink Mdl { get; }

        /// <summary>MDL → Layer 3 signals this station raised (MDL-NEGOTIATE
        /// Confirm / MDL-ERROR Indicate), in order.</summary>
        public ConcurrentQueue<MdlSignal> MdlSignals { get; }

        /// <summary>This station's §6.6 segmentation-reassembly shim — used by
        /// <see cref="TwoStationHarness.SubmitLarge"/> on the send side and wired
        /// into the dispatcher's upward-signal fan-out on the receive side.</summary>
        public SegmentationLayer Segmentation { get; }

        /// <summary>Deferred MDL-work queue (see <c>PeerWiring.MdlWork</c>).</summary>
        public Queue<Action> MdlWork { get; } = new();

        /// <summary>Payloads this station submitted via DL-DATA-request, in order.
        /// A <see cref="TwoStationHarness.SubmitLarge"/> call records the WHOLE
        /// (pre-segmentation) payload as one entry, so the convergence oracle
        /// compares it against the single reassembled delivery.</summary>
        public List<byte[]> Submitted { get; } = new();

        public Endpoint(
            string name, Ax25Session session, Ax25SessionContext context,
            ConcurrentQueue<DataLinkSignal> signals, Queue<Ax25Event> inbound,
            List<Ax25Frame> receivedFromPeer,
            Ax25ManagementDataLink mdl, ConcurrentQueue<MdlSignal> mdlSignals,
            SegmentationLayer segmentation)
        {
            Name = name; Session = session; Context = context;
            Signals = signals; Inbound = inbound; ReceivedFromPeer = receivedFromPeer;
            Mdl = mdl; MdlSignals = mdlSignals; Segmentation = segmentation;
        }

        public string State => Session.CurrentState;

        /// <summary>The MDL machine's current state — <c>Ready</c> or <c>Negotiating</c>.</summary>
        public string MdlState => Mdl.State;

        /// <summary>Payloads this station delivered upward (DL-DATA-indication),
        /// in order.</summary>
        public IReadOnlyList<byte[]> Delivered =>
            Signals.OfType<DataLinkDataIndication>().Select(s => s.Info.ToArray()).ToList();

        /// <summary>Layer-3 PIDs this station delivered upward (DL-DATA-indication),
        /// in order — paired one-to-one with <see cref="Delivered"/>. For a
        /// reassembled series this reflects the §6.6 reassembly's recovered PID
        /// (the original L3 PID under the default inner-PID format; PidNoLayer3
        /// under the figure-literal StrictlyFaithful format).</summary>
        public IReadOnlyList<byte> DeliveredPids =>
            Signals.OfType<DataLinkDataIndication>().Select(s => s.Pid).ToList();
    }

    private sealed class PeerWiring
    {
        public Queue<Ax25Event>? Target { get; set; }
        public List<Ax25Frame>? RxLog { get; set; }
        public Callsign? TargetLocal { get; set; }

        /// <summary>Accessor for the peer endpoint's MDL driver (a thunk because
        /// the peer is built after this wiring object is constructed). Lets the
        /// delivery path route an inbound XID/FRMR to the peer's MDL while it is
        /// negotiating — mirroring <see cref="Ax25Listener"/>.</summary>
        public Func<Ax25ManagementDataLink?>? Mdl { get; set; }

        /// <summary>The peer endpoint's deferred MDL-work queue. Frame deliveries
        /// destined for the MDL are enqueued here and drained by the pump, so an
        /// MDL reply is processed after the sender's own MDL transition commits
        /// (avoids same-machine PostEvent re-entrancy).</summary>
        public Queue<Action>? MdlWork { get; set; }
    }
}

/// <summary>Thrown by the harness / <see cref="InvariantChecker"/> when a
/// protocol safety or liveness invariant is violated.</summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}
