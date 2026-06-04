using System.Collections.Concurrent;
using Packet.Ax25.Sdl;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Ax25.Session;

/// <summary>
/// First-class AX.25 inbound-acceptance coordinator. Owns one
/// <see cref="IKissModem"/>, address-filters inbound frames against
/// <see cref="MyCall"/>, dispatches to the per-peer <see cref="Ax25Session"/>
/// (creating one on first contact — inbound SABM or outbound
/// <see cref="ConnectAsync(Callsign, CancellationToken)"/>), and surfaces
/// per-frame TX/RX events so monitor / promiscuous-capture UIs can tap
/// the channel.
/// </summary>
/// <remarks>
/// <para>
/// packet.net is being shaped into a packet-radio <em>node</em>: a station
/// that exists to accept inbound connections, not merely make outbound
/// ones. The Listener is the foundational piece of that shape — every
/// node-style consumer (BBS, gateway, automatic forwarder, the TUI) goes
/// through it instead of reinventing the inbound-pump / session-rebuild
/// loop the TUI's <c>SessionRunner</c> originally carried.
/// </para>
/// <para>
/// <b>Per-peer session cache.</b> The Listener keeps a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by remote
/// callsign. Sessions survive disconnect — they sit idle in the
/// Disconnected state, retaining their
/// <see cref="Ax25SessionContext"/> (and therefore their SRT / T1V
/// smoothing / sequence-variable history) for the next time that peer
/// connects in either direction. Eviction is LRU once
/// <see cref="Ax25ListenerOptions.MaxCachedPeers"/> is exceeded.
/// </para>
/// <para>
/// <b>Rejection path.</b> When
/// <see cref="AcceptIncoming"/> is <c>false</c> and a previously-unseen
/// peer sends SABM, the Listener builds a transient session with the
/// context's <see cref="Ax25SessionContext.AcceptIncoming"/> set to
/// <c>false</c>, posts the SABM into it, and discards the session as
/// soon as the SDL's figc4.1 t15 branch has emitted the DM response.
/// No session-accepted event fires for the dropped attempt.
/// </para>
/// </remarks>
public sealed class Ax25Listener : IAsyncDisposable
{
    private readonly IKissModem modem;
    private readonly Ax25ListenerOptions options;
    private readonly TimeProvider timeProvider;

    // The per-session AX.25 parameters applied to NEWLY-built sessions. Seeded
    // from the construction-time options, but live-reseedable via
    // UpdateSessionParameters so a node host can apply a config change to future
    // sessions without rebuilding the listener (which would drop live sessions).
    // Published by reference: BuildSession / EvictExcessLocked read a coherent
    // snapshot under a single volatile read; UpdateSessionParameters swaps the
    // whole record atomically. Existing cached sessions keep the context they
    // were built with — object identity preserved.
    private Ax25SessionParameters sessionParameters;
    private readonly ConcurrentDictionary<Callsign, CachedSession> sessions = new();
    private readonly LinkedList<Callsign> lruOrder = new();          // most-recently-used at the back
    private readonly Dictionary<Callsign, LinkedListNode<Callsign>> lruIndex = new();
    private readonly object cacheGate = new();
    private readonly CancellationTokenSource lifecycleCts = new();
    private readonly TaskCompletionSource<bool> pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? pumpTask;
    private int running;
    private int disposed;
    private bool acceptIncoming = true;

    /// <summary>
    /// One cached <see cref="Ax25Session"/> + its scheduler + its
    /// upward-signal queue (for outbound <see cref="ConnectAsync"/>
    /// callers waiting on DL-CONNECT-confirm), plus any per-session
    /// disposables.
    /// </summary>
    private sealed class CachedSession
    {
        public required Ax25Session Session { get; init; }
        public required SystemTimerScheduler Scheduler { get; init; }
        public required ConcurrentQueue<DataLinkSignal> Signals { get; init; }

        /// <summary>
        /// The session's management data-link (MDL) driver — runs the XID
        /// parameter-negotiation FSM. Started by the data-link's
        /// <c>MDL-NEGOTIATE Request</c> poke (raised after the UA on a v2.2
        /// connect); inbound XID-response / FRMR-of-XID frames are routed here
        /// while it is negotiating. Negotiated parameters land back on
        /// <see cref="Session"/>'s context.
        /// </summary>
        public required Ax25ManagementDataLink Mdl { get; init; }

        /// <summary>
        /// The session's §6.6 segmentation-reassembly shim. Sits at the DL
        /// primitive boundary: the send helper (<see cref="Ax25Listener.SendData"/>)
        /// runs an over-N1 payload through its segmenter, and the
        /// <c>SendUpward</c> fan-out runs every inbound DL-DATA indication
        /// through its reassembler (0x08 PID → reassemble, else pass through).
        /// One per session — it owns the per-session reassembly buffer.
        /// </summary>
        public required SegmentationLayer Segmentation { get; init; }
    }

    /// <summary>Our station identity. All inbound filtering checks against this.</summary>
    public Callsign MyCall => options.MyCall;

    /// <summary>True once <see cref="StartAsync"/> has been called and the inbound pump is running.</summary>
    public bool IsRunning => Volatile.Read(ref running) != 0;

    /// <summary>
    /// True if the listener will build a session for inbound SABMs.
    /// Flip to <c>false</c> to reject all new incoming (figc4.1 t15 →
    /// DM); existing sessions keep running. Default <c>true</c>.
    /// </summary>
    /// <remarks>
    /// The flag is read at SABM-arrival time on the inbound pump.
    /// In-flight inbound handshakes complete before the flag is
    /// observed; rejecting once a session is already cached is a
    /// disconnect, not a refusal.
    /// </remarks>
    public bool AcceptIncoming
    {
        get => Volatile.Read(ref acceptIncoming);
        set => Volatile.Write(ref acceptIncoming, value);
    }

    /// <summary>
    /// Fires once per peer-initiated connect, after the listener has
    /// built (or reused) the session and posted SABM. The session is
    /// already mid-handshake when this fires — consumers attach
    /// onData / onDisconnect handlers on the session's signal stream
    /// and proceed from there.
    /// </summary>
    public event EventHandler<Ax25SessionEventArgs>? SessionAccepted;

    /// <summary>
    /// Fires for every frame on the modem (TX + RX), with direction
    /// flag. Useful for monitor / promiscuous-capture UIs. Listener
    /// never filters this stream by addressing.
    /// </summary>
    public event EventHandler<Ax25FrameEventArgs>? FrameTraced;

    /// <summary>
    /// Construct a listener over the supplied modem with the supplied
    /// options. The listener does not start its inbound pump until
    /// <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="modem">KISS modem the listener attaches to. Reads frames via
    /// <see cref="IKissModem.ReadFramesAsync"/>; sends via <see cref="IKissModem.SendFrameAsync"/>.</param>
    /// <param name="options">Listener options — required <see cref="Ax25ListenerOptions.MyCall"/> and optional timing / cache knobs.</param>
    public Ax25Listener(IKissModem modem, Ax25ListenerOptions options)
        : this(modem, options, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-injection ctor: supply a custom <see cref="TimeProvider"/>
    /// so tests can drive T1/T2/T3 with a <c>FakeTimeProvider</c>.
    /// </summary>
    public Ax25Listener(IKissModem modem, Ax25ListenerOptions options, TimeProvider timeProvider)
    {
        this.modem = modem ?? throw new ArgumentNullException(nameof(modem));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        sessionParameters = Ax25SessionParameters.FromOptions(options);
    }

    /// <summary>
    /// Spin up the inbound pump. Returns once the pump task is up; the
    /// pump itself continues running in the background until
    /// <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
        {
            return Task.CompletedTask;
        }

        pumpTask = Task.Run(() => InboundPumpAsync(lifecycleCts.Token), CancellationToken.None);
        // Don't await pumpStarted in production — the pump signals
        // immediately on entering the loop, so awaiting it would only
        // add a context switch. Tests that want to be sure the pump is
        // ready can await `StartAsync` then await a brief settle.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The per-session AX.25 parameters that <em>new</em> sessions on this
    /// listener are currently built with. Reflects the construction-time
    /// <see cref="Ax25ListenerOptions"/> until <see cref="UpdateSessionParameters"/>
    /// changes them.
    /// </summary>
    public Ax25SessionParameters CurrentSessionParameters => Volatile.Read(ref sessionParameters);

    /// <summary>
    /// Live-reseed the per-session AX.25 parameters (T1V / T2 / T3 / N2 / k /
    /// max-cached-peers) used to build <em>future</em> sessions, without
    /// rebuilding the listener or disturbing any session that already exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the hot path the node host's reconcile uses for an
    /// AX.25-params-only config change: the running listener keeps its identity
    /// and every cached <see cref="Ax25Session"/> keeps its own object identity
    /// and its already-seeded <see cref="Ax25SessionContext"/> (and therefore its
    /// in-flight timers, sequence variables, and SRT/T1V smoothing). The new
    /// values apply only to sessions built <em>after</em> this call — a peer that
    /// connects in (or out) next picks them up; current QSOs are untouched.
    /// </para>
    /// <para>
    /// The swap is a single atomic reference publish, so a session being built
    /// concurrently on the inbound pump reads either the whole old record or the
    /// whole new one, never a torn mix. <see cref="MaxCachedPeers"/> takes effect
    /// on the next eviction pass (the next session add) — it never evicts a live
    /// session synchronously here.
    /// </para>
    /// </remarks>
    /// <param name="parameters">The new per-session parameters. <c>null</c>-valued
    /// members fall back to the engine's spec defaults, exactly as at construction.</param>
    public void UpdateSessionParameters(Ax25SessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureNotDisposed();
        Volatile.Write(ref sessionParameters, parameters);
    }

    /// <summary>
    /// Initiate an outbound connect against this listener's
    /// <see cref="MyCall"/> + the given remote. Reuses the cached
    /// session for that peer if one exists (preserves SRT / T1V
    /// history); otherwise builds one. Resolves to the session once
    /// <see cref="DataLinkConnectConfirm"/> arrives.
    /// </summary>
    /// <returns>
    /// The peer's <see cref="Ax25Session"/> once Connected. Throws
    /// <see cref="TimeoutException"/> if the connect doesn't complete
    /// within figc4.1's N2 × T1V budget; throws
    /// <see cref="InvalidOperationException"/> if the SDL responds with
    /// DM (peer refused) or torn down before the budget expired.
    /// </returns>
    public async Task<Ax25Session> ConnectAsync(Callsign remote, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (!IsRunning)
        {
            throw new InvalidOperationException("listener has not been started; call StartAsync() first.");
        }

        var cached = GetOrCreateSession(remote);
        TouchLru(remote);

        // Drain any stale signals queued from a previous lifecycle on
        // this cached session — otherwise we might fish out a stale
        // DataLinkConnectConfirm from the dictionary's last use.
        while (cached.Signals.TryDequeue(out _)) { }

        cached.Session.PostEvent(new DlConnectRequest());

        // figc4.2 budget — wait up to N2 * T1V for UA. Use the session's
        // negotiated values to give the right backstop on slow links.
        var budget = TimeSpan.FromMilliseconds(
            (cached.Session.Context.N2 + 1) * cached.Session.Context.T1V.TotalMilliseconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        while (!cts.IsCancellationRequested)
        {
            while (cached.Signals.TryDequeue(out var sig))
            {
                switch (sig)
                {
                    case DataLinkConnectConfirm:
                        RaiseSessionAccepted(cached.Session);
                        return cached.Session;
                    case DataLinkDisconnectIndication:
                    case DataLinkDisconnectConfirm:
                        throw new InvalidOperationException(
                            $"outbound connect to {remote} torn down before DL-CONNECT-confirm arrived (peer refused or link reset).");
                }
            }
            try { await Task.Delay(25, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"outbound connect to {remote} timed out after {budget.TotalSeconds:F1}s without DL-CONNECT-confirm.");
    }

    /// <summary>
    /// Send an upper-layer (Layer-3) payload over an established session,
    /// applying §6.6 segmentation at the DL boundary. This is the send-side
    /// counterpart to the receive-side reassembly wired into every session's
    /// upward-signal fan-out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the session has negotiated the segmenter
    /// (<see cref="Ax25SessionContext.SegmenterReassemblerEnabled"/>) and the
    /// payload exceeds N1, the payload is split into PID-0x08 I-frame segments
    /// and each is posted as its own <see cref="DlDataRequest"/>. Otherwise a
    /// single un-segmented request is posted. An over-N1 payload on a session
    /// that has <em>not</em> negotiated the segmenter throws
    /// <see cref="InvalidOperationException"/> — the request is rejected
    /// cleanly rather than truncated or sent oversize.
    /// </para>
    /// <para>
    /// Callers that want to send a raw, never-segmented request (e.g. a frame
    /// they have already segmented, or a control payload) can still post a
    /// <see cref="DlDataRequest"/> directly via
    /// <see cref="Ax25Session.PostEvent"/>; this helper is the
    /// segmentation-aware path.
    /// </para>
    /// </remarks>
    /// <param name="session">An <see cref="Ax25Session"/> previously returned
    /// by <see cref="ConnectAsync"/> or the <see cref="SessionAccepted"/> event.</param>
    /// <param name="data">The upper-layer payload.</param>
    /// <param name="pid">The Layer-3 PID for the (un-segmented) request. Defaults to
    /// <see cref="Ax25Frame.PidNoLayer3"/>.</param>
    /// <exception cref="ArgumentException">If <paramref name="session"/> is not a
    /// session this listener owns.</exception>
    /// <exception cref="InvalidOperationException">If the payload exceeds N1 and the
    /// segmenter has not been negotiated for this session.</exception>
    public void SendData(Ax25Session session, ReadOnlyMemory<byte> data, byte pid = Ax25Frame.PidNoLayer3)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNotDisposed();

        if (!sessions.TryGetValue(session.Context.Remote, out var cached) ||
            !ReferenceEquals(cached.Session, session))
        {
            throw new ArgumentException(
                "the supplied session is not owned by this listener (it was not produced by ConnectAsync / SessionAccepted, " +
                "or has been evicted from the cache).", nameof(session));
        }

        foreach (var request in cached.Segmentation.BuildSendRequests(data, pid))
        {
            session.PostEvent(request);
        }
    }

    /// <summary>Stop the inbound pump without disposing.</summary>
    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref running, 0) == 0) return;
        await lifecycleCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (pumpTask is { } pump) await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        lifecycleCts.Dispose();
        lock (cacheGate)
        {
            foreach (var cs in sessions.Values)
            {
                cs.Scheduler.Dispose();
            }
            sessions.Clear();
            lruOrder.Clear();
            lruIndex.Clear();
        }
    }

    // ─── Internals ────────────────────────────────────────────────────

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private async Task InboundPumpAsync(CancellationToken ct)
    {
        pumpStarted.TrySetResult(true);
        try
        {
            await foreach (var kiss in modem.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                if (kiss.Command != KissCommand.Data) continue;
                if (!Ax25Frame.TryParse(kiss.Payload, out var parsed)) continue;

                // Each per-frame step is isolated from the next so a
                // throwing event-handler or a misbehaving session
                // can't tear the pump down. A buggy consumer must not
                // be able to DoS the modem.
                try { TraceFrame(parsed, FrameDirection.Received); }
                catch (Exception) { /* swallowed: see Note on event-handler exceptions */ }

                try { DispatchInbound(parsed, kiss.Payload); }
                catch (Exception) { /* swallowed: see Note on event-handler exceptions */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        // Note on event-handler exceptions:
        // The Listener is a long-running infrastructure component —
        // a buggy SessionAccepted / FrameTraced subscriber must not
        // be allowed to crash the pump or leak through StopAsync /
        // DisposeAsync. We swallow synchronously here; future work
        // could surface them on a dedicated ListenerExceptionRaised
        // event so consumers that DO care can observe.
    }

    private void DispatchInbound(Ax25Frame parsed, ReadOnlyMemory<byte> payload)
    {
        // Frames not addressed to us: monitor-only (trace already
        // fired). Don't route to any session.
        if (!parsed.Destination.Callsign.Equals(MyCall))
        {
            return;
        }

        var peer = parsed.Source.Callsign;

        // Existing session — deliver to the cached state machine and
        // we're done. SABM from a peer we've seen before lands in the
        // cached session's Disconnected state and runs figc4.1 t14
        // just like a fresh one. We re-fire SessionAccepted in that
        // case so consumers can re-arm any per-session handlers they
        // attached the last time around.
        if (sessions.TryGetValue(peer, out var cached))
        {
            TouchLru(peer);

            // The routing parse (line ~268) was modulo-8 — we didn't yet know
            // which session, hence which modulo. Addresses precede the control
            // field and are modulo-independent, so routing is valid; but an
            // extended (modulo-128) I/S frame's 2-octet control field was
            // mis-read. Re-decode at the session's negotiated modulo before
            // classifying so N(S)/N(R)/PID/info land correctly.
            var frame = ReparseAtSessionModulo(parsed, payload, cached.Session.Context);
            var cachedClassified = Ax25FrameClassifier.Classify(frame);

            // XID / FRMR-of-XID routing — these belong to the MDL machine, not
            // the data-link session (the data-link Connected state has no XID
            // handler, and would FRMR-handle a FRMR as a full link reset):
            //
            //  • XID *command* → we are the responder: build + send the XID
            //    response (the un-transcribed figc5.1 responder path).
            //  • XID *response* while negotiating → we are the initiator: figc5.2
            //    applies the negotiated parameters.
            //  • FRMR while negotiating → figc5.2 §6.3.2 ¶1 v2.0 fallback.
            //
            // Outside those, frames fall through to the data-link session
            // unchanged (e.g. a stray XID response with no negotiation → the
            // data-link catch-all; a real FRMR on an established link → the
            // data-link FRMR handling).
            if (cachedClassified is XidReceived && frame.IsCommand)
            {
                cached.Mdl.RespondToXidCommand(frame);
                return;
            }
            if (cached.Mdl.IsNegotiating && cachedClassified is XidReceived)
            {
                cached.Mdl.OnXidReceived(frame);
                return;
            }
            if (cached.Mdl.IsNegotiating && cachedClassified is FrmrReceived)
            {
                cached.Mdl.OnFrmrReceived(frame);
                return;
            }

            bool wasDisconnected = cached.Session.CurrentState == "Disconnected";
            bool isReconnectSabm = wasDisconnected && (cachedClassified is SabmReceived or SabmeReceived);

            cached.Session.PostEvent(cachedClassified);

            if (isReconnectSabm && cached.Session.CurrentState == "Connected")
            {
                RaiseSessionAccepted(cached.Session);
            }
            return;
        }

        // No cached session — the establishment / transient paths below deal in
        // U-frames (SABM/SABME) or fall to the Disconnected catch-all (→ DM),
        // all correctly decoded at modulo-8: an unknown peer can't already have
        // an extended link with us, so no second pass is needed here.
        var classified = Ax25FrameClassifier.Classify(parsed);

        // No cached session and this isn't a SABM. Two cases:
        //
        //  (a) SABM-shape that we want to refuse (AcceptIncoming=false):
        //      route through a transient Disconnected session so the
        //      SDL's figc4.1 t15 branch fires DM. Don't cache.
        //
        //  (b) Any other frame kind (DISC / RR / RNR / REJ / SREJ / I /
        //      UI / FRMR / XID / TEST) addressed to us with no cached
        //      session: route through a transient Disconnected session.
        //      DISC has its own t13 (DM); UI has t11/t12 (UI_Check + DM
        //      on P=1); UA has t10 (DL_ERROR_indication C/D); everything
        //      else (RR/RNR/REJ/SREJ/I/FRMR/XID/TEST) falls to t05
        //      (all_other_commands → DM). We re-post the latter cluster
        //      as AllOtherCommands so t05's chain fires — the classifier
        //      produces specific event types (RrReceived etc.) which are
        //      correct for *cached* sessions in Connected/etc. but have
        //      no transition in Disconnected. The catch-all is named
        //      `all_other_commands` for exactly this case.
        //
        // The transient session uses the listener's current
        // AcceptIncoming for case (a)'s reject behaviour, and always
        // true for case (b) — the catch-alls don't gate on it.
        bool isSabmShaped = classified is SabmReceived || classified is SabmeReceived;

        if (isSabmShaped && AcceptIncoming)
        {
            // Accept path: build the session, cache it, fire the
            // consumer hook before posting SABM so consumers can attach
            // listeners on the session's signal stream before any
            // events flow.
            var built = BuildSession(peer, allowAccept: true);
            AddToCache(peer, built);
            options.ConfigureSession?.Invoke(built.Session);
            built.Session.PostEvent(classified);
            RaiseSessionAccepted(built.Session);
            return;
        }

        // Transient fall-through:
        //   SABM-shape with AcceptIncoming=false → figc4.1 t15 emits DM.
        //   DISC/UI/UA unknown peer            → specific Disconnected transition.
        //   RR/RNR/REJ/SREJ/I/FRMR/XID/TEST    → reclassify as AllOtherCommands
        //                                          so t05 fires DM.
        //
        // Build, post, dispose. No cache write, no SessionAccepted
        // event.
        var transient = BuildSession(peer, allowAccept: AcceptIncoming);
        var transientEvent = isSabmShaped
            ? classified
            : ReclassifyForDisconnectedCatchAll(classified, parsed);
        transient.Session.PostEvent(transientEvent);
        transient.Scheduler.Dispose();
    }

    /// <summary>
    /// Map an inbound classified event to the event the Disconnected
    /// SDL knows how to handle. Specific events handled in Disconnected
    /// (DISC/UI/UA/SABM/SABME) pass through unchanged; everything else
    /// (RR/RNR/REJ/SREJ/I/FRMR/XID/TEST) becomes <see cref="AllOtherCommands"/>
    /// so the SDL's t05 catch-all emits DM. See figc4.1 — the catch-all
    /// is named "all other commands" precisely for this case (the
    /// figure's per-frame-type column doesn't list RR/I-frame handling
    /// in Disconnected; they fall to the rightmost catch-all column).
    /// </summary>
    private static Ax25Event ReclassifyForDisconnectedCatchAll(Ax25Event classified, Ax25Frame frame)
        => classified switch
        {
            DiscReceived or UiReceived or UaReceived
                or SabmReceived or SabmeReceived => classified,
            _ => new AllOtherCommands(frame),
        };

    /// <summary>
    /// Re-decode an inbound I/S frame at a known session's negotiated modulo.
    /// The inbound pump parses every frame at modulo-8 for routing (the session,
    /// and thus the modulo, isn't known until the address is read) — which is
    /// always valid for the address fields but mis-reads an extended
    /// (modulo-128) I/S frame's 2-octet control field. Once the session is
    /// matched, this second pass re-parses the raw bytes at the session's
    /// modulo. Returns <paramref name="routed"/> unchanged for modulo-8 links
    /// and for U frames (1 octet in both modes); re-parses only an extended
    /// link's I/S frames, falling back to <paramref name="routed"/> if the
    /// second parse somehow fails (it can't, given the first succeeded).
    /// </summary>
    private static Ax25Frame ReparseAtSessionModulo(Ax25Frame routed, ReadOnlyMemory<byte> payload, Ax25SessionContext ctx)
    {
        if (!ctx.IsExtended) return routed;               // modulo-8 link: the routing parse was correct
        if (routed.IsExtendedControl) return routed;      // already 2-octet (defensive; the routing parse is mod-8)
        bool isUFrame = (routed.Control & 0x03) == 0x03;  // U frames are 1 octet in both modes
        if (isUFrame) return routed;
        return Ax25Frame.TryParse(payload.Span, Ax25ParseOptions.Lenient, extended: true, out var ext)
            ? ext
            : routed;
    }

    private void RaiseSessionAccepted(Ax25Session session)
    {
        var handler = SessionAccepted;
        if (handler is null) return;
        SafeInvoke(handler, new Ax25SessionEventArgs { Session = session });
    }

    private CachedSession GetOrCreateSession(Callsign peer)
    {
        if (sessions.TryGetValue(peer, out var existing))
        {
            return existing;
        }

        // BuildSession allocates; race-protect with cacheGate so two
        // concurrent ConnectAsync calls for the same peer don't end
        // up with two separate sessions.
        lock (cacheGate)
        {
            if (sessions.TryGetValue(peer, out existing))
            {
                return existing;
            }
            var built = BuildSession(peer, allowAccept: true);
            sessions[peer] = built;
            options.ConfigureSession?.Invoke(built.Session);
            UpdateLruLocked(peer);
            EvictExcessLocked();
            return built;
        }
    }

    private CachedSession BuildSession(Callsign peer, bool allowAccept)
    {
        // Snapshot the live per-session parameters once (single volatile read) so a
        // concurrent UpdateSessionParameters can't tear this build. New sessions
        // pick up the latest reseed; sessions already cached are never rebuilt.
        var sp = Volatile.Read(ref sessionParameters);

        var scheduler = new SystemTimerScheduler(timeProvider);
        var ctx = new Ax25SessionContext
        {
            Local = MyCall,
            Remote = peer,
            AcceptIncoming = allowAccept,
        };
        if (sp.N2 is { } n2)   ctx.N2 = n2;
        if (sp.K  is { } k)    ctx.K  = k;
        if (sp.T2  is { } t2)  ctx.T2  = t2;
        if (sp.T1V is { } t1v)
        {
            // Seed BOTH T1V and SRT so the value is coherent before any SDL
            // transition runs (T1V is what the T1 timer arms; SRT is what
            // figc4.7's Select_T1_Value smooths). The establishment path
            // (figc4.1 t13/t14) then runs `SRT := Initial Default; T1V := 2 * SRT`
            // unconditionally — which would clobber this seed back to the spec
            // default (m0lte/packet.net#292) — so we ALSO set the dispatcher's
            // InitialSrt to t1v/2 below, making `T1V := 2 * SRT` reproduce t1v.
            ctx.T1V = t1v;
            ctx.Srt = t1v / 2;
        }

        var signals = new ConcurrentQueue<DataLinkSignal>();
        var segmentation = new SegmentationLayer(ctx);

        Ax25Session? sessionRef = null;
        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            // Fire-and-forget — the dispatcher's frame sinks are sync,
            // so a sync write on the modem is fine here. The send
            // happens before TraceFrame so subscribers see TX in the
            // order frames hit the wire.
            _ = modem.SendFrameAsync(bytes);
            // Trace at this session's modulo so an extended (mod-128) I/S frame's
            // N(S)/N(R) render correctly in the monitor.
            if (Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, ctx.IsExtended, out var parsedTx))
            {
                TraceFrame(parsedTx, FrameDirection.Transmitted);
            }
        }

        // sendUpward fans out: into the listener's per-session queue
        // (so ConnectAsync can await DL-CONNECT-confirm without polling
        // the session state), AND through the session's public
        // DataLinkSignalEmitted event (so UI / consumer code can
        // subscribe push-style for DL-DATA-indication etc.).
        //
        // Receive-side segmentation seam (§2.4 / §6.6): every DL-DATA
        // indication passes through the reassembler first. A 0x08-PID segment
        // is consumed and only delivered when the series completes (the shim
        // returns null until the last segment); a non-segment indication
        // passes through unchanged. Non-DATA signals (connect/disconnect/error)
        // bypass the shim entirely. The dispatcher's
        // `DLDATAIndication => sendUpward(BuildDataIndication(tx))` is
        // untouched — the seam is here at the boundary, keeping the
        // dispatcher / SDL clean.
        void SendUpward(DataLinkSignal sig)
        {
            if (sig is DataLinkDataIndication dataInd)
            {
                var reassembled = segmentation.OnDataIndication(dataInd);
                if (reassembled is null) return;   // mid-series segment — nothing to deliver yet
                sig = reassembled;
            }
            signals.Enqueue(sig);
            sessionRef?.RaiseDataLinkSignal(sig);
        }

        // The session's MDL driver shares the session's scheduler (TM201 is a
        // distinct timer name, so it doesn't collide with T1/T2/T3) and the same
        // wire sink. Built before the data-link dispatcher so sendInternal can
        // route the MDL-NEGOTIATE-request poke straight to it. Negotiated
        // parameters mutate this session's context (ctx).
        var mdl = new Ax25ManagementDataLink(ctx, scheduler, SendBytes);

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    SendUpward,
            sendLinkMux:   _ => { },
            // The data-link figc4.6 UA-received path raises MDL-NEGOTIATE Request
            // after a successful v2.2 connect; hand it to the MDL driver to open
            // the XID exchange. (Other internal signals — push_I_frame_queue — are
            // queue-management that mutate ctx directly; nothing to do here.)
            sendInternal:  sig => { if (sig is MdlNegotiateRequestSignal) mdl.Negotiate(); },
            subroutines:   new DefaultSubroutineRegistry())
        {
            // Per-port timer overrides. InitialSrt seeds the establishment path's
            // `SRT := Initial Default; T1V := 2 * SRT` so a configured T1V actually
            // reaches the session's T1 timer (m0lte/packet.net#292) — without it,
            // the SDL resets T1V to 2×3000 ms on every connect. T3 arms the
            // inactive-link timer; T2 rides the dispatcher for the day an SDL with a
            // response-delay timer lands (today's figures arm no T2 — ack flush is
            // LM-SEIZE-driven), but threading it keeps the option meaningful and the
            // construction site uniform.
            InitialSrt  = sp.T1V is { } t1vSeed ? t1vSeed / 2 : ActionDispatcher.DefaultInitialSrt,
            // Seed the establishment path's `N2 := 10` so a configured N2 survives the
            // SABM/SABME connect that would otherwise reset it to the spec default —
            // the same clobber class as InitialSrt above (#292). Without this the
            // listener's (N2+1)·T1V connect backstop is always the 66 s spec maximum.
            InitialN2   = sp.N2 ?? ActionDispatcher.DefaultInitialN2,
            // Seed the establishment-path `T2 := 3000` and (mod-8) `k := 8` link-param
            // verbs from the configured values so they survive a connect that runs
            // Set_Version — the same #292/#300 clobber class. These verbs are inert on
            // the connect path in the current SDL (Set_Version isn't invoked there, so
            // the BuildSession ctx seeds already survive), but seeding them here keeps
            // every establishment-init verb consistent and forecloses a re-introduced
            // clobber if an upstream SDL revision puts Set_Version back on the path.
            InitialT2   = sp.T2 ?? ActionDispatcher.DefaultInitialT2,
            InitialK    = sp.K  ?? ActionDispatcher.DefaultInitialK,
            T3Duration  = sp.T3 ?? ActionDispatcher.DefaultT3,
            T2Duration  = sp.T2 ?? ActionDispatcher.DefaultT2,
        };

        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: DefaultTransitionMap,
            initialState: "Disconnected");
        sessionRef = session;

        return new CachedSession
        {
            Session = session,
            Scheduler = scheduler,
            Signals = signals,
            Mdl = mdl,
            Segmentation = segmentation,
        };
    }

    private void AddToCache(Callsign peer, CachedSession built)
    {
        lock (cacheGate)
        {
            sessions[peer] = built;
            UpdateLruLocked(peer);
            EvictExcessLocked();
        }
    }

    private void TouchLru(Callsign peer)
    {
        lock (cacheGate) UpdateLruLocked(peer);
    }

    private void UpdateLruLocked(Callsign peer)
    {
        if (lruIndex.TryGetValue(peer, out var node))
        {
            lruOrder.Remove(node);
        }
        var added = lruOrder.AddLast(peer);
        lruIndex[peer] = added;
    }

    private void EvictExcessLocked()
    {
        var maxCachedPeers = Volatile.Read(ref sessionParameters).MaxCachedPeers;
        while (lruOrder.Count > maxCachedPeers && lruOrder.First is { } oldest)
        {
            var evicted = oldest.Value;
            lruOrder.RemoveFirst();
            lruIndex.Remove(evicted);
            if (sessions.TryRemove(evicted, out var cs))
            {
                cs.Scheduler.Dispose();
            }
        }
    }

    private void TraceFrame(Ax25Frame frame, FrameDirection direction)
    {
        var handler = FrameTraced;
        if (handler is null) return;
        var args = new Ax25FrameEventArgs
        {
            Frame = frame,
            Direction = direction,
            Timestamp = timeProvider.GetUtcNow(),
        };
        SafeInvoke(handler, args);
    }

    /// <summary>
    /// Invoke each subscriber on a multicast delegate independently,
    /// swallowing any exception per-handler so one buggy subscriber
    /// can't suppress others. The Listener is infrastructure code —
    /// a faulty event consumer must not be able to break the
    /// inbound pump or starve other consumers.
    /// </summary>
    private void SafeInvoke<T>(EventHandler<T> handler, T args) where T : EventArgs
    {
        foreach (var del in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)del).Invoke(this, args); }
            catch (Exception) { /* swallowed; see XML doc */ }
        }
    }

    private static readonly Dictionary<string, IReadOnlyList<TransitionSpec>> DefaultTransitionMap = new()
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]        = DataLink_TimerRecovery.Transitions,
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };
}

/// <summary>
/// Options for <see cref="Ax25Listener"/>. <see cref="MyCall"/> is
/// required; everything else has spec-default behaviour.
/// </summary>
public sealed class Ax25ListenerOptions
{
    /// <summary>Local callsign. Inbound frames not addressed here are ignored at the session layer.</summary>
    public required Callsign MyCall { get; init; }

    /// <summary>
    /// Override the session's initial T1V (acknowledgement timer). If
    /// <c>null</c>, sessions use the spec default (2 × initial SRT = 6 s).
    /// </summary>
    /// <remarks>
    /// The value seeds both <see cref="Ax25SessionContext.T1V"/> and, via the
    /// dispatcher's <see cref="ActionDispatcher.InitialSrt"/> (= T1V/2), the
    /// establishment path's <c>SRT := Initial Default; T1V := 2 * SRT</c> — so a
    /// configured T1V survives the SABM/SABME connect that would otherwise reset
    /// it to the spec default (m0lte/packet.net#292). figc4.7's
    /// <c>Select_T1_Value</c> still smooths the *running* value from round-trip
    /// samples once frames flow; this only sets the starting point.
    /// </remarks>
    public TimeSpan? T1V { get; init; }

    /// <summary>
    /// Override the session-context default T2 (response-delay timer).
    /// If <c>null</c>, sessions use the spec default (3 s).
    /// </summary>
    public TimeSpan? T2 { get; init; }

    /// <summary>
    /// Override the dispatcher's T3 (inactive-link) timer duration.
    /// If <c>null</c>, sessions use the dispatcher default (30 s).
    /// </summary>
    public TimeSpan? T3 { get; init; }

    /// <summary>Override the spec-default <see cref="Ax25SessionContext.N2"/> (max retries; default 10).</summary>
    public int? N2 { get; init; }

    /// <summary>Override the spec-default <see cref="Ax25SessionContext.K"/> (send-window size; default 4 for mod-8).</summary>
    public int? K { get; init; }

    /// <summary>
    /// LRU cap on cached per-peer sessions. Default 64 — most node
    /// deployments sit well within that; the cap is a memory safety
    /// belt to keep a misbehaving / spam-SABM peer from creating
    /// unbounded sessions.
    /// </summary>
    public int MaxCachedPeers { get; init; } = 64;

    /// <summary>
    /// Optional hook called once per newly-built session, before any
    /// events flow into it. Use to attach <c>onData</c> /
    /// <c>onDisconnect</c> handlers on the session's signal stream
    /// before the SDL processes the inbound SABM that triggered
    /// session creation.
    /// </summary>
    public Action<Ax25Session>? ConfigureSession { get; init; }
}

/// <summary>
/// The subset of <see cref="Ax25ListenerOptions"/> that a running
/// <see cref="Ax25Listener"/> can live-reseed via
/// <see cref="Ax25Listener.UpdateSessionParameters"/> — the per-session AX.25
/// timing / window / cache knobs that only ever affect a session at the moment
/// it is built. Identity (<see cref="Ax25ListenerOptions.MyCall"/>) and the
/// <see cref="Ax25ListenerOptions.ConfigureSession"/> hook are deliberately
/// excluded: a callsign change is a different identity (a node-wide reset), and
/// the configure hook is fixed wiring, not a tunable.
/// </summary>
/// <remarks>
/// Each member mirrors the same-named member of <see cref="Ax25ListenerOptions"/>
/// exactly, including the "<c>null</c> ⇒ engine spec default" convention. This is
/// a value record so a reseed is a single atomic reference publish.
/// </remarks>
public sealed record Ax25SessionParameters
{
    /// <summary>Initial T1V (acknowledgement timer). <c>null</c> ⇒ spec default (2 × initial SRT = 6 s).</summary>
    public TimeSpan? T1V { get; init; }

    /// <summary>T2 (response-delay timer) override. <c>null</c> ⇒ spec default.</summary>
    public TimeSpan? T2 { get; init; }

    /// <summary>T3 (inactive-link timer) override. <c>null</c> ⇒ dispatcher default (30 s).</summary>
    public TimeSpan? T3 { get; init; }

    /// <summary>N2 (max retries) override. <c>null</c> ⇒ spec default (10).</summary>
    public int? N2 { get; init; }

    /// <summary>k (send-window size) override. <c>null</c> ⇒ spec default (4 for mod-8).</summary>
    public int? K { get; init; }

    /// <summary>LRU cap on cached per-peer sessions. Defaults to 64 (the <see cref="Ax25ListenerOptions"/> default).</summary>
    public int MaxCachedPeers { get; init; } = 64;

    /// <summary>Project the live-reseedable subset out of a full options record.</summary>
    public static Ax25SessionParameters FromOptions(Ax25ListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Ax25SessionParameters
        {
            T1V = options.T1V,
            T2 = options.T2,
            T3 = options.T3,
            N2 = options.N2,
            K = options.K,
            MaxCachedPeers = options.MaxCachedPeers,
        };
    }
}

/// <summary>Direction tag for a frame as it crosses the listener-modem boundary.</summary>
public enum FrameDirection
{
    /// <summary>The frame was emitted by the local stack and shipped to the modem.</summary>
    Transmitted,

    /// <summary>The frame was decoded off the modem's inbound stream.</summary>
    Received,
}

/// <summary>Carries a session for the <see cref="Ax25Listener.SessionAccepted"/> event.</summary>
public sealed class Ax25SessionEventArgs : EventArgs
{
    /// <summary>The session that was just (re)opened.</summary>
    public required Ax25Session Session { get; init; }
}

/// <summary>Carries a single frame + direction for the <see cref="Ax25Listener.FrameTraced"/> event.</summary>
public sealed class Ax25FrameEventArgs : EventArgs
{
    /// <summary>The parsed frame, in either direction.</summary>
    public required Ax25Frame Frame { get; init; }

    /// <summary>Whether this was TX or RX.</summary>
    public required FrameDirection Direction { get; init; }

    /// <summary>The wall-clock time the listener observed the frame, per its <see cref="TimeProvider"/>.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
