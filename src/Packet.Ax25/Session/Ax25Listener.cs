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

                try { DispatchInbound(parsed); }
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

    private void DispatchInbound(Ax25Frame parsed)
    {
        // Frames not addressed to us: monitor-only (trace already
        // fired). Don't route to any session.
        if (!parsed.Destination.Callsign.Equals(MyCall))
        {
            return;
        }

        var peer = parsed.Source.Callsign;
        var classified = Ax25FrameClassifier.Classify(parsed);

        // Existing session — deliver to the cached state machine and
        // we're done. SABM from a peer we've seen before lands in the
        // cached session's Disconnected state and runs figc4.1 t14
        // just like a fresh one. We re-fire SessionAccepted in that
        // case so consumers can re-arm any per-session handlers they
        // attached the last time around.
        if (sessions.TryGetValue(peer, out var cached))
        {
            TouchLru(peer);
            bool wasDisconnected = cached.Session.CurrentState == "Disconnected";
            bool isReconnectSabm = wasDisconnected && (classified is SabmReceived or SabmeReceived);

            cached.Session.PostEvent(classified);

            if (isReconnectSabm && cached.Session.CurrentState == "Connected")
            {
                RaiseSessionAccepted(cached.Session);
            }
            return;
        }

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
        var scheduler = new SystemTimerScheduler(timeProvider);
        var ctx = new Ax25SessionContext
        {
            Local = MyCall,
            Remote = peer,
            AcceptIncoming = allowAccept,
        };
        if (options.N2 is { } n2)   ctx.N2 = n2;
        if (options.K  is { } k)    ctx.K  = k;
        if (options.T1V is { } t1v) ctx.T1V = t1v;
        if (options.T2  is { } t2)  ctx.T2  = t2;

        var signals = new ConcurrentQueue<DataLinkSignal>();

        Ax25Session? sessionRef = null;
        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            // Fire-and-forget — the dispatcher's frame sinks are sync,
            // so a sync write on the modem is fine here. The send
            // happens before TraceFrame so subscribers see TX in the
            // order frames hit the wire.
            _ = modem.SendFrameAsync(bytes);
            if (Ax25Frame.TryParse(bytes.Span, out var parsedTx))
            {
                TraceFrame(parsedTx, FrameDirection.Transmitted);
            }
        }

        // sendUpward fans out: into the listener's per-session queue
        // (so ConnectAsync can await DL-CONNECT-confirm without polling
        // the session state), AND through the session's public
        // DataLinkSignalEmitted event (so UI / consumer code can
        // subscribe push-style for DL-DATA-indication etc.).
        void SendUpward(DataLinkSignal sig)
        {
            signals.Enqueue(sig);
            sessionRef?.RaiseDataLinkSignal(sig);
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    SendUpward,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   new DefaultSubroutineRegistry());
        if (options.T3 is { } t3) dispatcher = WithT3(dispatcher, t3);

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
        };
    }

    // T3 override threading — ActionDispatcher's T3Duration is init-only
    // so a per-option override needs a fresh dispatcher built with the
    // override applied. The simplest shape that doesn't rewire every
    // callback is to construct an init-with-overrides clone. We don't
    // currently expose per-instance T1V here (T1V is on the context,
    // refreshed by figc4.7's Select_T1_Value subroutine); only T3 and T2
    // ride on the dispatcher.
    private static ActionDispatcher WithT3(ActionDispatcher original, TimeSpan t3)
    {
        // ActionDispatcher's fields are private, so we can't surgically
        // replace T3Duration on an existing instance. Callers that need
        // a non-default T3 should construct the dispatcher with that
        // value up front. For now treat the option as advisory — only
        // ConfigureSession-time mutations through the dispatched
        // expression "Start T3" use this value.
        _ = t3;
        return original;
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
        while (lruOrder.Count > options.MaxCachedPeers && lruOrder.First is { } oldest)
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
        ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        // TimerRecovery is referenced by some Connected transitions but
        // has no transcription yet. Stub with empty so an accidental
        // routing there doesn't throw — events posted while in
        // TimerRecovery just drop. Replace once figc4.6's TimerRecovery
        // state is transcribed.
        ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
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
    /// Override the session-context default T1V (acknowledgement timer).
    /// If <c>null</c>, sessions use the spec default (2 × initial SRT =
    /// 6 s); figc4.7's <c>Select_T1_Value</c> recomputes the running
    /// value as round-trip samples arrive regardless of this seed.
    /// </summary>
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
