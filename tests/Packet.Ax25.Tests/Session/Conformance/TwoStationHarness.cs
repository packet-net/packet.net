using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
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
        bool extended = false)
    {
        var nodeA = new Callsign("M0LTEA", 1);
        var nodeB = new Callsign("M0LTEB", 2);
        var time  = new FakeTimeProvider();
        var link  = new Channel();
        var t1v   = TimeSpan.FromMilliseconds(t1Ms);

        var a = BuildEndpoint(nodeA, nodeB, time, link, srej, k, t1Ms, n2, t2Ms, extended, out var aPeer);
        var b = BuildEndpoint(nodeB, nodeA, time, link, srej, k, t1Ms, n2, t2Ms, extended, out var bPeer);
        aPeer.Target = b.Inbound;          bPeer.Target = a.Inbound;
        aPeer.RxLog  = b.ReceivedFromPeer; bPeer.RxLog  = a.ReceivedFromPeer;
        return new TwoStationHarness(a, b, link, time, t1v, TimeSpan.FromMilliseconds(t2Ms));
    }

    private static Endpoint BuildEndpoint(
        Callsign local, Callsign remote, FakeTimeProvider time, Channel link,
        bool srej, int k, int t1Ms, int n2, int t2Ms, bool extended, out PeerWiring peer)
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
        };
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var inbound = new Queue<Ax25Event>();
        var rxLog = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            if (!Ax25Frame.TryParse(bytes.Span, out var parsed)) return;
            if (link.ShouldDrop(parsed)) return;
            if (peerLocal.TargetLocal is { } expected && !parsed.Destination.Callsign.Equals(expected)) return;
            peerLocal.RxLog?.Add(parsed);
            peerLocal.Target?.Enqueue(Ax25FrameClassifier.Classify(parsed));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:  spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:  signals.Enqueue,
            // Model a contention-free medium: grant LM-SEIZE immediately so the
            // figc4.4 delayed-ack (Set Ack Pending + LM-SEIZE Request → flush the
            // RR on LM-SEIZE-confirm) actually flushes. Without this the link can
            // only ack via T1 polls — which is why the legacy rigs (all of which
            // stub sendLinkMux) never exercised autonomous delayed-ack.
            sendLinkMux: signal => { if (signal is LinkMultiplexerSeizeRequest) inbound.Enqueue(new LmSeizeConfirm()); },
            sendInternal: _ => { },
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
        return new Endpoint(local.ToString(), session, ctx, signals, inbound, rxLog);
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

    /// <summary>Disconnect from <paramref name="from"/>; asserts both reach
    /// Disconnected.</summary>
    public void Disconnect(Endpoint from)
    {
        from.Session.PostEvent(new DlDisconnectRequest());
        PumpToQuiescence();
        if (CheckAfterEachStep) CheckInvariants();
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
    }

    public sealed class Endpoint
    {
        public string Name { get; }
        public Ax25Session Session { get; }
        public Ax25SessionContext Context { get; }
        public ConcurrentQueue<DataLinkSignal> Signals { get; }
        public Queue<Ax25Event> Inbound { get; }
        public List<Ax25Frame> ReceivedFromPeer { get; }

        /// <summary>Payloads this station submitted via DL-DATA-request, in order.</summary>
        public List<byte[]> Submitted { get; } = new();

        public Endpoint(
            string name, Ax25Session session, Ax25SessionContext context,
            ConcurrentQueue<DataLinkSignal> signals, Queue<Ax25Event> inbound,
            List<Ax25Frame> receivedFromPeer)
        {
            Name = name; Session = session; Context = context;
            Signals = signals; Inbound = inbound; ReceivedFromPeer = receivedFromPeer;
        }

        public string State => Session.CurrentState;

        /// <summary>Payloads this station delivered upward (DL-DATA-indication),
        /// in order.</summary>
        public IReadOnlyList<byte[]> Delivered =>
            Signals.OfType<DataLinkDataIndication>().Select(s => s.Info.ToArray()).ToList();
    }

    private sealed class PeerWiring
    {
        public Queue<Ax25Event>? Target { get; set; }
        public List<Ax25Frame>? RxLog { get; set; }
        public Callsign? TargetLocal { get; set; }
    }
}

/// <summary>Thrown by the harness / <see cref="InvariantChecker"/> when a
/// protocol safety or liveness invariant is violated.</summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}
