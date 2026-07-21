using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Sdl;
using Packet.Core;
using Packet.Ax25.Transport;

namespace Packet.Ax25.Session;

/// <summary>
/// First-class AX.25 inbound-acceptance coordinator. Owns one
/// <see cref="IAx25Transport"/>, address-filters inbound frames against
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
public sealed partial class Ax25Listener : IAsyncDisposable
{
    // U-frame control-octet classification (§4.3.3). The P/F bit (0x10) is masked
    // out so a TEST with P=1 (a command soliciting a response) and P=0 both match.
    private const byte UFrameControlMask = 0xEF;   // mask off the P/F bit
    private const byte TestControl = 0xE3;   // TEST, P/F-masked (mirrors AxPinger)

    private readonly IAx25Transport modem;
    private readonly ILogger logger;
    private readonly string portName;

    // The transport's optional TX-completion capability (ACKMODE), probed once at construction.
    // Non-null means "this transport CAN attempt confirmed-TX"; a runtime NotSupportedException
    // still latches ackmodeUnsupported as the backstop (a wrapping adapter exposes the capability
    // even when the underlying modem turns out not to support it).
    private readonly ITxCompletionTransport? txCompletion;

    // Native carrier-sense CSMA gate (OQ-012): the link-multiplexer consults it before every
    // keyup and holds the transmission while the channel is busy. Off by default — with no
    // source (Ax25ListenerOptions.CarrierSense == null) it always reports clear, so every send
    // is byte-for-byte the prior fire-and-forget path and the SDL transition behaviour is
    // untouched. A radio-attached node port injects its IRadioControl DCD (via RadioCarrierSense)
    // through that parity-tracked option; the coming Nino KISS DCD extension lands in the same gate.
    private readonly CarrierSenseGate carrierSenseGate;
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
    // Sessions are keyed by the (local, remote) callsign PAIR: with local aliases (below) the
    // same remote can hold one link to MyCall and another to an app callsign simultaneously —
    // a remote-only key would conflate them. The console/MyCall path is unchanged semantically
    // (its key is just (MyCall, remote) now).
    private readonly ConcurrentDictionary<SessionKey, CachedSession> sessions = new();
    private readonly LinkedList<SessionKey> lruOrder = new();        // most-recently-used at the back
    private readonly Dictionary<SessionKey, LinkedListNode<SessionKey>> lruIndex = new();
    private readonly object cacheGate = new();
    // Additional local callsigns this listener answers for (inbound SABM/TEST) and may
    // originate from — the multi-callsign seam the node's RHPv2 server registers app
    // callsigns into (an RHP client's `bind` is what adds one). Value = refcount, so two
    // independent registrations of the same callsign compose.
    private readonly ConcurrentDictionary<Callsign, int> localAliases = new();
    private readonly CancellationTokenSource lifecycleCts = new();
    private readonly TaskCompletionSource<bool> pumpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? pumpTask;
    private int running;
    private int disposed;
    private bool acceptIncoming = true;

    /// <summary>
    /// One cached <see cref="Ax25Session"/> + its scheduler + its
    /// upward-signal queue (for outbound <see cref="ConnectAsync(Callsign, System.Threading.CancellationToken)"/>
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
    /// A point-in-time snapshot of the live sessions this listener currently holds,
    /// for read-only surfaces (the node control API's <c>/sessions</c>). Each session
    /// exposes its <see cref="Ax25Session.CurrentState"/> + <see cref="Ax25Session.Context"/>
    /// (Remote, V(S)/V(R), window K, retry count, smoothed RTT). Reading does not mutate
    /// the cache or disturb a session.
    /// </summary>
    public IReadOnlyList<Ax25Session> ActiveSessions
    {
        get
        {
            var list = new List<Ax25Session>();
            foreach (var cached in sessions.Values)
            {
                list.Add(cached.Session);
            }
            return list;
        }
    }

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
    /// Register an additional local callsign this listener answers for: an inbound SABM (or
    /// connectionless TEST) addressed to it is accepted exactly as one addressed to
    /// <see cref="MyCall"/>, with the session's <see cref="Ax25SessionContext.Local"/> set to
    /// the alias — and <see cref="ConnectAsync(Callsign, Callsign, CancellationToken)"/> can
    /// originate from it. This is the multi-callsign seam the node's RHPv2 server uses to
    /// answer for application callsigns (a client's <c>bind</c> registers one). Refcounted:
    /// each <see cref="AddLocalAlias"/> is balanced by one <see cref="RemoveLocalAlias"/>.
    /// </summary>
    public void AddLocalAlias(Callsign alias)
        => localAliases.AddOrUpdate(alias, 1, (_, n) => n + 1);

    /// <summary>
    /// Remove one registration of <paramref name="alias"/> (see <see cref="AddLocalAlias"/>).
    /// The listener stops answering for the callsign when the last registration is removed;
    /// live sessions on it keep running until they disconnect (their cache key keeps routing
    /// their frames — removal only stops <em>new</em> inbound acceptance).
    /// </summary>
    public void RemoveLocalAlias(Callsign alias)
    {
        while (localAliases.TryGetValue(alias, out var n))
        {
            if (n <= 1)
            {
                if (localAliases.TryRemove(new KeyValuePair<Callsign, int>(alias, n)))
                {
                    return;
                }
            }
            else if (localAliases.TryUpdate(alias, n - 1, n))
            {
                return;
            }
        }
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
    /// <param name="modem">The AX.25 frame transport the listener attaches to. Reads frames via
    /// <see cref="IAx25Transport.ReceiveAsync"/>; sends via <see cref="IAx25Transport.SendAsync"/>.</param>
    /// <param name="options">Listener options — required <see cref="Ax25ListenerOptions.MyCall"/> and optional timing / cache knobs.</param>
    public Ax25Listener(IAx25Transport modem, Ax25ListenerOptions options)
        : this(modem, options, TimeProvider.System, null)
    {
    }

    /// <summary>
    /// Test-injection ctor: supply a custom <see cref="TimeProvider"/> so tests can drive
    /// T1/T2/T3 with a <c>FakeTimeProvider</c>. The native carrier-sense CSMA source (if any)
    /// is taken from <see cref="Ax25ListenerOptions.CarrierSense"/> — the first-class,
    /// parity-tracked medium-access seam — so the link-multiplexer holds every keyup while the
    /// channel is busy (see <see cref="CarrierSenseGate"/>). A <c>null</c> source (the default)
    /// is the always-clear degenerate gate: transmissions are never deferred, so behaviour is
    /// byte-for-byte the same as before.
    /// </summary>
    public Ax25Listener(
        IAx25Transport modem,
        Ax25ListenerOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        this.modem = modem ?? throw new ArgumentNullException(nameof(modem));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? NullLogger.Instance;
        portName = options.PortName ?? "?";
        // Probe the optional confirmed-TX (ACKMODE) capability once; null ⇒ never attempt it.
        txCompletion = modem as ITxCompletionTransport;
        // The native medium-access gate, from the parity-tracked listener option.
        // No source ⇒ always-clear ⇒ no deferral.
        carrierSenseGate = new CarrierSenseGate(options.CarrierSense, timeProvider);
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

        LogStarted(portName, MyCall.ToString());
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
    /// whole new one, never a torn mix. <see cref="Ax25SessionParameters.MaxCachedPeers"/> takes effect
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
    public Task<Ax25Session> ConnectAsync(Callsign remote, CancellationToken ct = default)
        => ConnectAsync(remote, MyCall, ct);

    /// <summary>
    /// Initiate an outbound connect originating from <paramref name="local"/> instead of
    /// <see cref="MyCall"/> — the multi-callsign origination the node's RHPv2 server uses to
    /// dial out as an application's callsign. The session's cache key is the (local, remote)
    /// pair, so the same remote can hold simultaneous links to MyCall and to an alias; the
    /// inbound filter routes the peer's replies by that pair (no alias registration needed
    /// for an outbound link — its live cache key admits them).
    /// </summary>
    /// <remarks>
    /// Whether this dial prefers AX.25 v2.2 (SABME / mod-128, degrading to v2.0 for
    /// non-v2.2 peers) or initiates a plain v2.0 (SABM / mod-8) connect follows the
    /// listener's <see cref="Ax25ListenerOptions.PreferExtendedConnect"/> default. Use
    /// <see cref="ConnectAsync(Callsign, Callsign, bool, CancellationToken)"/> to override
    /// per dial.
    /// </remarks>
    public Task<Ax25Session> ConnectAsync(Callsign remote, Callsign local, CancellationToken ct = default)
        => ConnectAsync(remote, local, CurrentSessionParameters.PreferExtendedConnect, ct);

    /// <summary>
    /// Initiate an outbound connect, explicitly choosing the version to prefer:
    /// <paramref name="extended"/> <c>true</c> attempts v2.2 (SABME / mod-128, with the
    /// FRMR (<see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>) and DM
    /// (<see cref="Ax25SessionQuirks.Ax25Spec48DmRejectionDegradesToV20"/>) fallbacks
    /// degrading to v2.0 for peers that can't); <c>false</c> initiates a plain v2.0
    /// (SABM / mod-8) connect. This is the per-call override of the listener's
    /// <see cref="Ax25ListenerOptions.PreferExtendedConnect"/> default. The pre-connect
    /// XID probe follows the listener's <see cref="Ax25ListenerOptions.PreConnectXidNegotiatesSrej"/>
    /// default; use
    /// <see cref="ConnectAsync(Callsign, Callsign, bool, bool, CancellationToken)"/> to
    /// override that per dial too.
    /// </summary>
    public Task<Ax25Session> ConnectAsync(Callsign remote, Callsign local, bool extended, CancellationToken ct = default)
        => ConnectAsync(remote, local, extended, CurrentSessionParameters.PreConnectXidNegotiatesSrej, ct);

    /// <summary>
    /// As <see cref="ConnectAsync(Callsign, Callsign, bool, CancellationToken)"/>, but also
    /// overrides the listener's <see cref="Ax25ListenerOptions.PreConnectXidNegotiatesSrej"/>
    /// per dial. <paramref name="preConnectXidNegotiatesSrej"/> only takes effect on a mod-8
    /// dial (<paramref name="extended"/> = <c>false</c>) — the v2.2/SABME path negotiates XID
    /// post-UA. The node's per-peer capability cache uses this to skip the pre-SABM XID probe
    /// for a neighbour it already knows does not answer one (go-back-N), or to force it.
    /// </summary>
    public async Task<Ax25Session> ConnectAsync(Callsign remote, Callsign local, bool extended, bool preConnectXidNegotiatesSrej, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (!IsRunning)
        {
            throw new InvalidOperationException("listener has not been started; call StartAsync() first.");
        }

        var key = new SessionKey(local, remote);
        var cached = GetOrCreateSession(key);
        TouchLru(key);

        LogConnecting(portName, local.ToString(), remote.ToString(), extended ? "v2.2/SABME" : "v2.0/SABM");

        // Drain any stale signals queued from a previous lifecycle on
        // this cached session — otherwise we might fish out a stale
        // DataLinkConnectConfirm from the dictionary's last use.
        while (cached.Signals.TryDequeue(out _)) { }

        // Choose the version this dial initiates BEFORE posting DL-CONNECT-request:
        // IsExtended drives the figc4.7 Establish_Data_Link modulo branch (SABME vs
        // SABM) and, via Ax25Spec44, routes the connect through AwaitingV22Connection
        // (figc4.6) so the FRMR/DM v2.0 fallbacks (Ax25Spec45 / Ax25Spec48) are
        // reachable. Set only on the outbound dial — the inbound answerer adopts the
        // peer's version from the SABM/SABME it receives (figc4.1). A cached session
        // re-dialled after a prior fallback dropped it to mod-8 is re-armed here, so
        // every dial starts from the caller's chosen preference.
        cached.Session.Context.IsExtended = extended;

        // LinBPQ SREJ accommodation (PreConnectXidNegotiatesSrej): on a mod-8 dial,
        // run an XID command/response BEFORE the SABM to negotiate Selective Reject.
        // BPQ does mod-8 SREJ but only honours an XID that PRECEDES the SABM (its
        // ProcessXIDCommand runs on the no-active-link path and sets Ver2point2; an
        // XID on an established link is ignored). The v2.2 figures negotiate XID
        // post-UA instead — which never reaches BPQ's responder — so SREJ-to-BPQ
        // specifically needs this pre-SABM exchange. Proven on the wire
        // (SrejXidViaNetsim). Safe regardless of peer: if no XID response arrives in
        // the budget, we fall through to a plain SABM (go-back-N link). Skipped on
        // the extended (SABME) path — that uses the figc4.6 post-UA MDL negotiation.
        if (!extended && preConnectXidNegotiatesSrej)
        {
            LogPreConnectXid(portName, local.ToString(), remote.ToString());
            await NegotiateSrejBeforeConnectAsync(cached, ct).ConfigureAwait(false);
            LogXidOutcome(portName, local.ToString(), remote.ToString(),
                cached.Session.Context.SrejEnabled ? "confirmed" : "no response",
                cached.Session.Context.SrejEnabled ? "SREJ enabled" : "go-back-N");
        }

        cached.Session.PostEvent(new DlConnectRequest());

        // figc4.2 budget — wait up to N2 * T1V for UA. Use the session's
        // negotiated values to give the right backstop on slow links.
        var budget = TimeSpan.FromMilliseconds(
            (cached.Session.Context.N2 + 1) * cached.Session.Context.T1V.TotalMilliseconds);
        // Budget timer on the listener's TimeProvider so the connect backstop is
        // drivable under a FakeTimeProvider in tests (production uses
        // TimeProvider.System, so this is behaviourally identical there).
        using var budgetCts = new CancellationTokenSource(budget, timeProvider);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);

        // Drain queued upward signals: return the session on DL-CONNECT-confirm,
        // throw on teardown, or null if nothing decisive is queued yet.
        Ax25Session? DrainForConfirm()
        {
            while (cached.Signals.TryDequeue(out var sig))
            {
                switch (sig)
                {
                    case DataLinkConnectConfirm:
                        LogConnected(portName, local.ToString(), remote.ToString(),
                            cached.Session.Context.IsExtended ? "v2.2/mod-128" : "v2.0/mod-8");
                        RaiseSessionAccepted(cached.Session);
                        return cached.Session;
                    case DataLinkDisconnectIndication:
                    case DataLinkDisconnectConfirm:
                        LogConnectRefused(portName, local.ToString(), remote.ToString());
                        throw new InvalidOperationException(
                            $"outbound connect to {remote} torn down before DL-CONNECT-confirm arrived (peer refused or link reset).");
                }
            }
            return null;
        }

        while (!cts.IsCancellationRequested)
        {
            if (DrainForConfirm() is { } connected)
            {
                return connected;
            }

            try { await Task.Delay(25, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // An explicit caller-cancel is a cancellation, not a timeout — honour it first.
        ct.ThrowIfCancellationRequested();

        // Final drain before declaring failure: a DL-CONNECT-confirm enqueued in
        // the last poll window — after the loop's inner drain, as the budget
        // expired and Task.Delay was cancelled — would otherwise be lost to a
        // spurious timeout even though the connect actually succeeded.
        if (DrainForConfirm() is { } lateConfirm)
        {
            return lateConfirm;
        }

        LogConnectTimeout(portName, local.ToString(), remote.ToString(), budget.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        throw new TimeoutException(
            $"outbound connect to {remote} timed out after {budget.TotalSeconds:F1}s without DL-CONNECT-confirm.");
    }

    /// <summary>
    /// Pre-SABM SREJ negotiation for the mod-8 dial (the LinBPQ accommodation gated
    /// by <see cref="Ax25ListenerOptions.PreConnectXidNegotiatesSrej"/>). Sets the
    /// context SREJ-capable so the management-data-link's XID offer advertises
    /// SREJ + SREJ-multiframe at mod-8, opens the negotiation, and waits a bounded
    /// time for the peer's XID response (which the inbound router applies via the MDL,
    /// setting <see cref="Ax25SessionContext.SrejEnabled"/>) before returning so the
    /// caller can post DL-CONNECT-request. A peer that does not answer XID leaves the
    /// MDL to exhaust its TM201 retries; we cap the wait and proceed to a plain SABM
    /// (go-back-N) regardless — the dial is never blocked by a non-XID peer.
    /// </summary>
    private async Task NegotiateSrejBeforeConnectAsync(CachedSession cached, CancellationToken ct)
    {
        var ctx = cached.Session.Context;

        // Offer SREJ: DefaultOfferFor reads SrejEnabled to advertise SREJ + the
        // OPSREJMult bit BPQ's XID responder requires. The peer's XID response is
        // applied by the inbound router (XidNegotiator.ApplyNegotiated), which sets
        // SrejEnabled to the MUTUAL result — true only if the peer also offered SREJ.
        ctx.SrejEnabled = true;
        ctx.ImplicitReject = false;

        // Track the negotiation outcome so a peer that never answers XID (TM201
        // give-up: MDL-ERROR, link context untouched) does not leave us wrongly
        // SREJ-enabled. A confirm means the peer's response was merged in (SrejEnabled
        // now holds the true mutual value); anything else → force go-back-N.
        var confirmed = false;
        void OnMdl(object? _, MdlSignal sig)
        {
            if (sig is MdlNegotiateConfirmSignal)
            {
                confirmed = true;
            }
        }
        cached.Mdl.MdlSignalEmitted += OnMdl;
        try
        {
            cached.Mdl.Negotiate();

            // Optimistic short probe, NOT a full connection-retry budget. A peer that
            // does pre-session XID (BPQ) answers on the FIRST frame — its XID response is
            // immediate on the no-active-link path. A peer that doesn't (another PDN, a
            // dumb v2.0 TNC) never answers, so waiting the full (N2+1)·T1V establishment
            // budget (≈ up to 12 s) just stalls every mod-8 dial to it — including NET/ROM
            // interlinks — before the SABM fallback. So wait only ~2·T1V (one command +
            // one retry / a loss margin), floored at 1.5 s so a clean link gets a fair
            // shot and capped at 3.5 s so a silent peer degrades to go-back-N promptly.
            // The MDL leaves Negotiating on the XID response (success), a FRMR (v2.0
            // fallback), or give-up. (Adaptive per-neighbour reuse is the capability cache,
            // §5.G — remember who answers and skip the probe entirely.)
            var budget = TimeSpan.FromMilliseconds(
                Math.Min(3_500, Math.Max(1_500, 2 * ctx.T1V.TotalMilliseconds)));
            using var budgetCts = new CancellationTokenSource(budget, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);

            while (!linked.IsCancellationRequested)
            {
                if (!cached.Mdl.IsNegotiating)
                {
                    break;
                }

                try { await Task.Delay(25, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            cached.Mdl.MdlSignalEmitted -= OnMdl;
        }

        // No confirmed XID negotiation (silent peer / give-up) → the peer can't do
        // SREJ; revert to go-back-N so we never put SREJ on the wire unilaterally.
        if (!confirmed)
        {
            ctx.SrejEnabled = false;
            ctx.ImplicitReject = true;
        }

        // Honour an explicit caller cancel; a budget expiry just proceeds to SABM.
        ct.ThrowIfCancellationRequested();
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
    /// by <see cref="ConnectAsync(Callsign, System.Threading.CancellationToken)"/> or the <see cref="SessionAccepted"/> event.</param>
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

        if (!sessions.TryGetValue(new SessionKey(session.Context.Local, session.Context.Remote), out var cached) ||
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

    /// <summary>
    /// Send a connectionless UI (unproto) frame on this port's modem — the send
    /// path connected-mode <see cref="SendData"/> is not: it bypasses the session
    /// layer entirely. This is what an upper layer uses to transmit
    /// promiscuously-heard broadcasts (NET/ROM NODES routing broadcasts ride a UI
    /// frame: PID 0xCF, AX.25 destination the literal text callsign
    /// <c>NODES</c>). The source callsign is this listener's <see cref="MyCall"/>;
    /// the frame is built via the strict <see cref="Ax25Frame.Ui"/> factory (the
    /// outbound construction path stays spec-faithful) and traced as
    /// <see cref="FrameDirection.Transmitted"/> so the monitor sees it.
    /// </summary>
    /// <param name="destination">The UI frame's AX.25 destination (e.g. the literal
    /// <c>NODES</c> callsign for a NET/ROM routing broadcast).</param>
    /// <param name="info">The UI frame's information field.</param>
    /// <param name="pid">The Layer-3 PID (e.g. <see cref="Ax25Frame.PidNetRom"/>).</param>
    /// <param name="ct">Cancellation for the modem send.</param>
    public Task SendUiAsync(
        Callsign destination, ReadOnlyMemory<byte> info, byte pid = Ax25Frame.PidNoLayer3, CancellationToken ct = default)
        => SendUiAsync(MyCall, destination, info, pid, ct);

    /// <summary>
    /// Send a connectionless UI (unproto) frame originating from an <b>explicit source</b>
    /// callsign instead of <see cref="MyCall"/> — the multi-callsign origination the node's
    /// RHPv2 server uses to emit a DGRAM datagram (the wire's <c>sendto</c>) as an application's
    /// bound callsign (e.g. an IP-over-AX.25 UI frame, pid <c>0xCC</c>, or a native beacon / APRS
    /// frame, pid <c>0xF0</c>). Like the <see cref="SendUiAsync(Callsign, ReadOnlyMemory{byte}, byte, CancellationToken)"/>
    /// overload it bypasses the session layer; the frame is built via the strict
    /// <see cref="Ax25Frame.Ui(Callsign, Callsign, ReadOnlySpan{byte}, byte, bool, bool, IEnumerable{Callsign})"/>
    /// factory (which takes an explicit source) and traced as <see cref="FrameDirection.Transmitted"/>.
    /// </summary>
    /// <param name="source">The UI frame's AX.25 source (originating) callsign.</param>
    /// <param name="destination">The UI frame's AX.25 destination.</param>
    /// <param name="info">The UI frame's information field.</param>
    /// <param name="pid">The Layer-3 PID.</param>
    /// <param name="ct">Cancellation for the modem send.</param>
    public async Task SendUiAsync(
        Callsign source, Callsign destination, ReadOnlyMemory<byte> info, byte pid = Ax25Frame.PidNoLayer3, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        var frame = Ax25Frame.Ui(destination, source, info.Span, pid, isCommand: true);
        await SendAndTraceAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a strict outbound frame on the modem, then trace it as
    /// <see cref="FrameDirection.Transmitted"/>. The trace MUST come after the send
    /// so the monitor's TX order matches the wire — centralised here so that ordering
    /// (and the comment explaining it) lives in one place rather than being repeated
    /// at every connectionless send site. The per-session <c>SendBytes</c> path keeps
    /// its own copy because it interleaves T1 re-arming with the send.
    /// </summary>
    private async Task SendAndTraceAsync(Ax25Frame frame, CancellationToken ct = default)
    {
        // Native carrier-sense CSMA (OQ-012): hold the keyup while the channel is busy.
        // WaitForClearAsync completes synchronously when there is no source or the channel is
        // clear/unknown, so this connectionless path is unchanged when no radio DCD is wired.
        var csmaWait = await carrierSenseGate.WaitForClearAsync(ct).ConfigureAwait(false);
        if (csmaWait.TotalMilliseconds > 50)
        {
            LogCsmaWait(portName, (long)csmaWait.TotalMilliseconds);
        }
        await modem.SendAsync(frame.ToBytes(), ct).ConfigureAwait(false);
        TraceFrame(frame, FrameDirection.Transmitted);
        LogConnectionlessTx(portName, frame.Source.Callsign.ToString(), frame.Destination.Callsign.ToString(), Ax25FrameDescriber.Describe(frame));
    }

    /// <summary>
    /// Send a connectionless AX.25 <b>TEST command</b> frame (§4.3.4.2) to
    /// <paramref name="destination"/> with the given information field — the
    /// "axping" probe. A spec-compliant responder echoes the information field back
    /// in a TEST <em>response</em>; the caller correlates that response (via
    /// <see cref="FrameTraced"/>) to measure round-trip time. Like
    /// <see cref="SendUiAsync(Callsign, ReadOnlyMemory{byte}, byte, CancellationToken)"/> this bypasses the session layer entirely (no
    /// connection needed); the source is this listener's <see cref="MyCall"/>, the
    /// frame is built via the strict <see cref="Ax25Frame.Test"/> factory, and it is
    /// traced as <see cref="FrameDirection.Transmitted"/>. <paramref name="pollFinal"/>
    /// defaults to the P bit set (a command soliciting a response). Not every node
    /// implements TEST — a peer that doesn't simply never responds (the caller sees a
    /// timeout / loss), which is not an error.
    /// </summary>
    public async Task SendTestAsync(
        Callsign destination, ReadOnlyMemory<byte> info, bool pollFinal = true, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        var frame = Ax25Frame.Test(destination, MyCall, info.Span, isCommand: true, pollFinal: pollFinal);
        await SendAndTraceAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>Stop the inbound pump without disposing.</summary>
    public async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref running, 0) == 0)
        {
            return;
        }

        LogStopped(portName);
        await lifecycleCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (pumpTask is { } pump)
            {
                await pump.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

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

    // TX-complete→T1: latched true on the first NotSupportedException from
    // ITxCompletionTransport.SendAwaitingCompletionAsync so a non-ACKMODE modem
    // costs one failed attempt, not one per frame. (RestartT1OnTxComplete is
    // documented to require an ACKMODE-capable port; this is the
    // graceful-degradation backstop.)
    private volatile bool ackmodeUnsupported;

    /// <summary>
    /// Frames whose transmission (re)arms T1 in the figures — an I-frame, an
    /// enquiry (S-frame command with P=1), or a mode-setting command
    /// (SABM/SABME/DISC, whose AwaitingConnection/Release retries T1 drives).
    /// Only these get the ACKMODE TX-complete→T1 treatment; responses and
    /// plain acks never extend our own response timer.
    /// </summary>
    private static bool FrameArmsT1(Ax25Frame frame)
    {
        if ((frame.Control & 0x01) == 0)
        {
            return true; // I-frame
        }
        if ((frame.Control & 0x03) == 0x01)
        {
            return frame.IsCommand && frame.PollFinal; // RR/RNR/REJ command P=1 — an enquiry
        }
        var uBase = frame.Control & 0xEF;
        return uBase is 0x2F or 0x6F or 0x43; // SABM / SABME / DISC
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private async Task InboundPumpAsync(CancellationToken ct)
    {
        pumpStarted.TrySetResult(true);
        try
        {
            await foreach (var frame in modem.ReceiveAsync(ct).ConfigureAwait(false))
            {
                // The transport already delivers only AX.25 frames (a KISS transport drops
                // non-Data KISS commands itself), so there is no wire-protocol filter here.
                // Parse under the port's configured options (read live, so a
                // reseed applies from the next frame). A frame the options
                // reject is dropped here — before tracing and dispatch — so a
                // Strict port is deaf to it end-to-end (no session can open
                // from it, and the monitor/NET-ROM taps don't see it either).
                var parseOptions = Volatile.Read(ref sessionParameters).ParseOptions ?? Ax25ParseOptions.Lenient;
                if (!Ax25Frame.TryParse(frame.Ax25.Span, parseOptions, out var parsed))
                {
                    continue;
                }

                // Each per-frame step is isolated from the next so a
                // throwing event-handler or a misbehaving session
                // can't tear the pump down. A buggy consumer must not
                // be able to DoS the modem.
                try { TraceFrame(parsed, FrameDirection.Received); }
                catch (Exception) { /* swallowed: see Note on event-handler exceptions */ }

                try { DispatchInbound(parsed, frame.Ax25, parseOptions); }
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

    private void DispatchInbound(Ax25Frame parsed, ReadOnlyMemory<byte> payload, Ax25ParseOptions parseOptions)
    {
        // Frames not addressed to us: monitor-only (trace already fired). "Us" is MyCall, any
        // registered local alias (app callsigns — see AddLocalAlias), or the local side of a
        // live session (an outbound link originated FROM an alias keeps receiving its replies
        // even if the alias was deregistered mid-session).
        var local = parsed.Destination.Callsign;
        var key = new SessionKey(local, parsed.Source.Callsign);
        if (!local.Equals(MyCall) && !localAliases.ContainsKey(local) && !sessions.ContainsKey(key))
        {
            return;
        }

        LogRx(portName, parsed.Source.Callsign.ToString(), local.ToString(), Ax25FrameDescriber.Describe(parsed));

        // Connectionless TEST (§4.3.4.2) is link-independent and must be handled BEFORE any
        // session routing — see TryInterceptConnectionlessTest for the full rationale.
        if (TryInterceptConnectionlessTest(parsed))
        {
            return;
        }

        // Existing session — deliver to the cached state machine and we're done.
        if (TryRouteToCachedSession(key, parsed, payload, parseOptions))
        {
            return;
        }

        // No cached session — run the establishment / transient handling.
        HandleNoCachedSession(key, local, parsed.Source.Callsign, parsed);
    }

    /// <summary>
    /// Intercept a connectionless TEST frame (§4.3.4.2) addressed to us, handled BEFORE
    /// any session routing. TEST is link-independent: it must never enter the session
    /// machine (where it would fall to the Disconnected t05 catch-all and provoke a
    /// spurious DM, or disturb a live QSO's state).
    /// <list type="bullet">
    /// <item>TEST <em>command</em> → we are the responder: reply with a TEST response
    /// echoing the information field (the "axping" answer).</item>
    /// <item>TEST <em>response</em> → the echo to our own axping. The AxPinger initiator
    /// correlates it via <c>FrameTraced</c>, which already fired upstream on the pump
    /// (before <see cref="DispatchInbound"/>) — so we simply absorb it here. Routing it
    /// onward would only emit a connectionless DM (the t05 catch-all), which is
    /// spec-noise back at a station that just answered our probe.</item>
    /// </list>
    /// Returns <c>true</c> when the frame was a TEST and has been absorbed (the caller
    /// returns without touching a session — observation-safe); <c>false</c> otherwise.
    /// </summary>
    private bool TryInterceptConnectionlessTest(Ax25Frame parsed)
    {
        if ((parsed.Control & UFrameControlMask) != TestControl)
        {
            return false;
        }
        if (parsed.IsCommand)
        {
            RespondToTest(parsed);
        }
        return true;
    }
    /// <summary>
    /// Route an inbound frame to its cached session, if one exists for <paramref name="key"/>.
    /// Re-decodes at the session's negotiated modulo, splits XID/FRMR off to the MDL machine,
    /// and posts the data-link event (re-firing <c>SessionAccepted</c> on a reconnect SABM that
    /// reaches Connected). Returns <c>true</c> when a cached session handled the frame;
    /// <c>false</c> when there is no cached session (the caller falls through to establishment).
    /// </summary>
    private bool TryRouteToCachedSession(
        SessionKey key, Ax25Frame parsed, ReadOnlyMemory<byte> payload, Ax25ParseOptions parseOptions)
    {
        // Existing session — deliver to the cached state machine and we're done. SABM from a
        // peer we've seen before lands in the cached session's Disconnected state and runs
        // figc4.1 t14 just like a fresh one. We re-fire SessionAccepted in that case so
        // consumers can re-arm any per-session handlers they attached the last time around.
        if (!sessions.TryGetValue(key, out var cached))
        {
            return false;
        }

        TouchLru(key);

        // The routing parse (line ~268) was modulo-8 — we didn't yet know
        // which session, hence which modulo. Addresses precede the control
        // field and are modulo-independent, so routing is valid; but an
        // extended (modulo-128) I/S frame's 2-octet control field was
        // mis-read. Re-decode at the session's negotiated modulo before
        // classifying so N(S)/N(R)/PID/info land correctly.
        var frame = ReparseAtSessionModulo(parsed, payload, cached.Session.Context, parseOptions);
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
            return true;
        }
        if (cached.Mdl.IsNegotiating && cachedClassified is XidReceived)
        {
            cached.Mdl.OnXidReceived(frame);
            return true;
        }
        if (cached.Mdl.IsNegotiating && cachedClassified is FrmrReceived)
        {
            cached.Mdl.OnFrmrReceived(frame);
            return true;
        }

        bool wasDisconnected = cached.Session.CurrentState == "Disconnected";
        bool isReconnectSabm = wasDisconnected && (cachedClassified is SabmReceived or SabmeReceived);

        cached.Session.PostEvent(cachedClassified);

        if (isReconnectSabm && cached.Session.CurrentState == "Connected")
        {
            RaiseSessionAccepted(cached.Session);
        }
        return true;
    }

    /// <summary>
    /// Handle an inbound frame addressed to us for which no session is cached: the connection
    /// establishment (SABM accept), the pre-session XID responder, and the transient
    /// fall-through that emits the appropriate Disconnected-state response (DM / etc.).
    /// </summary>
    private void HandleNoCachedSession(SessionKey key, Callsign local, Callsign peer, Ax25Frame parsed)
    {
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
        //      UI / FRMR / XID) addressed to us with no cached session:
        //      route through a transient Disconnected session. DISC has
        //      its own t13 (DM); UI has t11/t12 (UI_Check + DM on P=1);
        //      UA has t10 (DL_ERROR_indication C/D); everything else
        //      (RR/RNR/REJ/SREJ/I/FRMR/XID) falls to t05
        //      (all_other_commands → DM). We re-post the latter cluster
        //      as AllOtherCommands so t05's chain fires — the classifier
        //      produces specific event types (RrReceived etc.) which are
        //      correct for *cached* sessions in Connected/etc. but have
        //      no transition in Disconnected. The catch-all is named
        //      `all_other_commands` for exactly this case. (TEST never
        //      reaches here — it is intercepted connectionlessly above. An
        //      XID *command* with AcceptIncoming=true is likewise intercepted
        //      above — the pre-session responder path — and never reaches
        //      here; an XID *response* with no negotiation, or any XID with
        //      AcceptIncoming=false, still does and falls to t05.)
        //
        // The transient session uses the listener's current
        // AcceptIncoming for case (a)'s reject behaviour, and always
        // true for case (b) — the catch-alls don't gate on it.
        bool isSabmShaped = classified is SabmReceived || classified is SabmeReceived;

        // A deregistered alias whose session is gone: only the live-session key kept the
        // frame past the filter; with no cached session and no registration, don't build a
        // transient responder AS the alias — fall silent (we no longer answer for it).
        if (!local.Equals(MyCall) && !localAliases.ContainsKey(local))
        {
            return;
        }

        // Pre-session XID *command* (a peer doing pre-SABM negotiation to us, no
        // active link yet). §4.3.3.7 makes answering an XID command unconditional —
        // "A station receiving an XID command returns an XID response unless a UA is
        // pending or a FRMR condition exists" — and §6.3.2 has negotiation happen
        // *before* the connection so the subsequent SABM's link adopts the negotiated
        // parameters. The MDL (Annex C5.3) is a connection-independent machine, so
        // answering here is plain spec-compliant behaviour, not a pragmatic opt-in.
        //
        // The defect this closes is purely inbound routing: without this, the command
        // fell through to a transient session, got reclassified to all_other_commands,
        // and the peer's pre-connect XID went unanswered (figc4.1 t05 → DM) — stalling
        // PDN↔PDN NET/ROM mod-8 interlinks where the initiator opens with XID.
        //
        // We build + cache a real session (NOT transient): object identity is the
        // staging mechanism — the (local,remote)-keyed cache persists the negotiated
        // link context across the XID→SABM sequence, and the inbound SABM's figc4.1
        // t14 "Set Version 2.0" clears only IsExtended (it does NOT touch SrejEnabled),
        // so a staged SrejEnabled survives into the connection. We seed the context
        // SREJ-capable so the responder's DefaultOfferFor advertises SREJ; the §6.3.2
        // lesser-of merge in RespondToXidCommand reverts it if the peer didn't offer
        // SREJ. Gate only on AcceptIncoming, consistent with the SABM-accept path: if
        // we won't accept the connection we shouldn't half-open from an XID.
        if (classified is XidReceived && parsed.IsCommand && AcceptIncoming)
        {
            var xidSession = BuildSession(local, peer, allowAccept: true);
            AddToCache(key, xidSession);
            options.ConfigureSession?.Invoke(xidSession.Session);

            // Seed SREJ-capable so DefaultOfferFor advertises SREJ in our response;
            // the lesser-of merge reverts this if the peer's offer lacked SREJ.
            xidSession.Session.Context.SrejEnabled = true;
            xidSession.Session.Context.ImplicitReject = false;

            // Build + send the F=1 XID response (the figc5.1 responder path). DO NOT
            // raise SessionAccepted — there's no DL-CONNECT yet; the following SABM
            // raises it. DO NOT dispose the scheduler — the session must persist for
            // that SABM, and the responder arms no timer, so nothing leaks.
            xidSession.Mdl.RespondToXidCommand(parsed);
            return;
        }

        if (isSabmShaped && AcceptIncoming)
        {
            // Accept path: build the session, cache it, fire the
            // consumer hook before posting SABM so consumers can attach
            // listeners on the session's signal stream before any
            // events flow. The session's Local is the callsign the SABM
            // was addressed to (MyCall or a registered alias).
            LogInboundAccept(portName, peer.ToString(), local.ToString(), classified is SabmeReceived ? "SABME" : "SABM");
            var built = BuildSession(local, peer, allowAccept: true);
            AddToCache(key, built);
            options.ConfigureSession?.Invoke(built.Session);
            built.Session.PostEvent(classified);
            RaiseSessionAccepted(built.Session);
            return;
        }

        // Transient fall-through:
        //   SABM-shape with AcceptIncoming=false → figc4.1 t15 emits DM.
        //   DISC/UI/UA unknown peer            → specific Disconnected transition.
        //   RR/RNR/REJ/SREJ/I/FRMR/XID         → reclassify as AllOtherCommands
        //                                          so t05 fires DM. (TEST is
        //                                          intercepted connectionlessly
        //                                          above and never arrives here.)
        //
        // Build, post, dispose. No cache write, no SessionAccepted
        // event.
        if (isSabmShaped)
        {
            LogInboundReject(portName, peer.ToString(), local.ToString(), classified is SabmeReceived ? "SABME" : "SABM");
        }
        var transient = BuildSession(local, peer, allowAccept: AcceptIncoming);
        var transientEvent = isSabmShaped
            ? classified
            : ReclassifyForDisconnectedCatchAll(classified, parsed);
        transient.Session.PostEvent(transientEvent);
        transient.Scheduler.Dispose();
    }

    /// <summary>
    /// Answer an inbound connectionless TEST <em>command</em> (§4.3.4.2) with a
    /// TEST <em>response</em> that echoes the command's information field verbatim.
    /// The response's F bit mirrors the command's P bit, the source is this
    /// station's <see cref="MyCall"/>, and it is built via the strict
    /// <see cref="Ax25Frame.Test"/> factory (the outbound construction path stays
    /// spec-faithful). The frame is sent on the modem and traced as
    /// <see cref="FrameDirection.Transmitted"/>, mirroring
    /// <see cref="SendUiAsync(Callsign, ReadOnlyMemory{byte}, byte, CancellationToken)"/> / <see cref="SendTestAsync"/>.
    /// </summary>
    /// <remarks>
    /// Connectionless + observation-safe: it never touches a session's state, so a
    /// TEST exchange cannot create, disturb, or tear down any link. Called from the
    /// sync inbound pump (<see cref="DispatchInbound"/>); the modem send is async,
    /// so we materialise the info field (the inbound buffer is reused after the
    /// pump moves on) and fire-and-forget an awaited send on a tracked Task with a
    /// try/catch — never block the pump, never leave an unobserved Task. A failed
    /// send (modem torn down mid-flight) is swallowed: the inbound pump must not
    /// die because a connectionless courtesy reply couldn't go out.
    /// </remarks>
    private void RespondToTest(Ax25Frame command)
    {
        // Materialise the echoed info: the pump may recycle the inbound buffer
        // once DispatchInbound returns, but the send below runs after that.
        var echo = command.Info.ToArray();
        var responder = command.Source.Callsign;
        // Respond AS the callsign the TEST was addressed to — MyCall normally, the alias when
        // an app callsign was pinged (the station "at" that callsign answers, not the node).
        var respondAs = command.Destination.Callsign;
        // F bit of the response mirrors the P bit of the command (§4.3.4.2).
        bool pollFinal = command.PollFinal;

        _ = Task.Run(async () =>
        {
            try
            {
                var frame = Ax25Frame.Test(
                    destination: responder, source: respondAs, info: echo,
                    isCommand: false, pollFinal: pollFinal);
                await SendAndTraceAsync(frame).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallowed: a connectionless TEST reply that can't go out (modem
                // disposed / write failed) must not tear the inbound pump down.
            }
        });
    }

    /// <summary>
    /// Map an inbound classified event to the event the Disconnected
    /// SDL knows how to handle. Specific events handled in Disconnected
    /// (DISC/UI/UA/SABM/SABME) pass through unchanged; everything else
    /// (RR/RNR/REJ/SREJ/I/FRMR/XID) becomes <see cref="AllOtherCommands"/>
    /// so the SDL's t05 catch-all emits DM. (TEST is connectionless and never
    /// routed into a session — see the intercept in <see cref="DispatchInbound"/>.)
    /// See figc4.1 — the catch-all
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
    private static Ax25Frame ReparseAtSessionModulo(
        Ax25Frame routed, ReadOnlyMemory<byte> payload, Ax25SessionContext ctx, Ax25ParseOptions parseOptions)
    {
        if (!ctx.IsExtended)
        {
            return routed;               // modulo-8 link: the routing parse was correct
        }

        if (routed.IsExtendedControl)
        {
            return routed;      // already 2-octet (defensive; the routing parse is mod-8)
        }

        bool isUFrame = (routed.Control & 0x03) == 0x03;  // U frames are 1 octet in both modes
        if (isUFrame)
        {
            return routed;
        }
        // Same options as the routing parse — a frame can't get stricter or
        // looser treatment just because its session negotiated mod-128.
        return Ax25Frame.TryParse(payload.Span, parseOptions, extended: true, out var ext)
            ? ext
            : routed;
    }

    private void RaiseSessionAccepted(Ax25Session session)
    {
        var handler = SessionAccepted;
        if (handler is null)
        {
            return;
        }

        SafeInvoke(handler, new Ax25SessionEventArgs { Session = session });
    }

    private CachedSession GetOrCreateSession(SessionKey key)
    {
        if (sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // BuildSession allocates; race-protect with cacheGate so two
        // concurrent ConnectAsync calls for the same peer don't end
        // up with two separate sessions.
        lock (cacheGate)
        {
            if (sessions.TryGetValue(key, out existing))
            {
                return existing;
            }
            var built = BuildSession(key.Local, key.Remote, allowAccept: true);
            sessions[key] = built;
            options.ConfigureSession?.Invoke(built.Session);
            UpdateLruLocked(key);
            EvictExcessLocked();
            return built;
        }
    }

    // Build the per-session context, seeding the port's configured parameters. Pure and
    // side-effect-free (no closures, no scheduler) — the build-time seed of N1/N2/K/T1V/T2
    // and the session quirks. A later reseed never reaches into an existing session's context.
    private static Ax25SessionContext SeedSessionContext(
        Callsign local, Callsign peer, bool allowAccept, Ax25SessionParameters sp)
    {
        var ctx = new Ax25SessionContext
        {
            Local = local,         // MyCall, or a registered alias / origination override
            Remote = peer,
            AcceptIncoming = allowAccept,
        };
        // Seed the port's configured session quirks before any SDL transition
        // runs. Like the timing knobs below, this is build-time only — a later
        // reseed never reaches into an existing session's context.
        if (sp.Quirks is { } quirks)
        {
            ctx.Quirks = quirks;
        }

        if (sp.N2 is { } n2)
        {
            ctx.N2 = n2;
        }

        if (sp.K is { } k)
        {
            ctx.K = k;
        }

        if (sp.N1 is { } n1)
        {
            ctx.N1 = n1;   // PACLEN: seed the offered N1 (XID can still lower it)
        }

        if (sp.T2 is { } t2)
        {
            ctx.T2 = t2;
        }

        if (sp.T1V is { } t1v)
        {
            // Seed BOTH T1V and SRT so the value is coherent before any SDL
            // transition runs (T1V is what the T1 timer arms; SRT is what
            // figc4.7's Select_T1_Value smooths). The establishment path
            // (figc4.1 t13/t14) then runs `SRT := Initial Default; T1V := 2 * SRT`
            // unconditionally — which would clobber this seed back to the spec
            // default (packet-net/packet.net#292) — so we ALSO set the dispatcher's
            // InitialSrt to t1v/2 below, making `T1V := 2 * SRT` reproduce t1v.
            ctx.T1V = t1v;
            ctx.Srt = t1v / 2;
        }

        return ctx;
    }

    private CachedSession BuildSession(Callsign local, Callsign peer, bool allowAccept)
    {
        // Snapshot the live per-session parameters once (single volatile read) so a
        // concurrent UpdateSessionParameters can't tear this build. New sessions
        // pick up the latest reseed; sessions already cached are never rebuilt.
        var sp = Volatile.Read(ref sessionParameters);

        var scheduler = new SystemTimerScheduler(timeProvider);
        var ctx = SeedSessionContext(local, peer, allowAccept, sp);

        var signals = new ConcurrentQueue<DataLinkSignal>();
        var segmentation = new SegmentationLayer(ctx);

        Ax25Session? sessionRef = null;
        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            // Parse first (needed both for the monitor trace and the T1-arming
            // classification below). Trace at this session's modulo so an extended
            // (mod-128) I/S frame's N(S)/N(R) render correctly in the monitor.
            // Deliberately Lenient even when the port's inbound ParseOptions are
            // stricter: these bytes came from our own strict frame factories, and
            // the only thing strictness could achieve here is hiding our own
            // transmission from the monitor.
            Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, ctx.IsExtended, out var parsedTx);

            // Fire-and-forget — the dispatcher's frame sinks are sync, so a sync
            // write on the modem is fine here. The send happens before TraceFrame
            // so subscribers see TX in the order frames hit the wire.
            //
            // TX-complete→T1 (RestartT1OnTxComplete): the SDL arms T1 the moment a
            // frame is handed to the modem — but behind a buffering TNC on a slow
            // channel, enqueue and cleared-the-air differ by up to (queue depth ×
            // airtime), so T1 has to be sized for worst-case queue + airtime +
            // the peer's T2 + the ack's airtime, and Select_T1's SRT smoothing
            // measures queue noise instead of round trips. When the option is on,
            // a T1-arming frame is sent in ACKMODE instead, and the TNC's
            // TX-completion echo pushes a still-running T1's deadline out to
            // (TX-complete + T1V) — the timer the figures intended, measured from
            // when the frame actually finished transmitting. If the SDL already
            // stopped T1 (the ack won the race), RearmIfRunning touches nothing.
            // Echo loss / no ACKMODE support degrades to enqueue-time semantics.
            //
            // Native carrier-sense CSMA (OQ-012): both send paths first pass through the
            // link-multiplexer's carrier-sense gate (SendGatedAsync / the wait inside
            // SendAndRearmT1Async), which holds the keyup while the channel is busy and keys up
            // when it clears. The gate completes synchronously when there is no carrier-sense
            // source or the channel is clear, so with no radio DCD wired every send is the same
            // synchronous fire-and-forget as before — no reordering, no extra hop, and the SDL
            // transition behaviour is untouched. This is the native medium-access seam: the stack
            // itself owns the deferral (source supplied via Ax25ListenerOptions.CarrierSense).
            if (options.RestartT1OnTxComplete && txCompletion is not null && !ackmodeUnsupported && parsedTx is not null && FrameArmsT1(parsedTx))
            {
                _ = SendAndRearmT1Async(bytes.ToArray());
            }
            else
            {
                _ = SendGatedAsync(bytes);
            }

            if (parsedTx is not null)
            {
                TraceFrame(parsedTx, FrameDirection.Transmitted);
                LogTx(portName, ctx.Local.ToString(), ctx.Remote.ToString(), Ax25FrameDescriber.Describe(parsedTx));
            }

            // Carrier-sense-gated fire-and-forget send: await a clear channel, then key up.
            async Task SendGatedAsync(ReadOnlyMemory<byte> frame)
            {
                var wait = await carrierSenseGate.WaitForClearAsync().ConfigureAwait(false);
                if (wait.TotalMilliseconds > 50)
                {
                    LogCsmaWait(portName, (long)wait.TotalMilliseconds);
                }
                await modem.SendAsync(frame).ConfigureAwait(false);
            }

            async Task SendAndRearmT1Async(byte[] frame)
            {
                try
                {
                    // Hold the ACKMODE keyup on a busy channel too (native CSMA), then send
                    // awaiting completion.
                    var wait = await carrierSenseGate.WaitForClearAsync().ConfigureAwait(false);
                    if (wait.TotalMilliseconds > 50)
                    {
                        LogCsmaWait(portName, (long)wait.TotalMilliseconds);
                    }
                    // txCompletion is non-null here (gated by the caller's probe), but the
                    // underlying transport may still NotSupport at runtime (a wrapping adapter
                    // exposes the capability even when the modem turns out not to have it).
                    await txCompletion!.SendAwaitingCompletionAsync(frame).ConfigureAwait(false);
                    scheduler.RearmIfRunning(Ax25TimerNames.T1, ctx.T1V);
                }
                catch (NotSupportedException)
                {
                    // The transport has no ACKMODE — latch and stop trying (the frame
                    // was NOT sent by the failed call, so send it plainly now — the gate was
                    // already consulted above).
                    ackmodeUnsupported = true;
                    _ = modem.SendAsync(frame);
                }
                catch
                {
                    // Echo timeout / link bounce: the frame is on the wire (ACKMODE
                    // wraps a real send); T1 stays as the SDL armed it.
                }
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
                if (reassembled is null)
                {
                    return;   // mid-series segment — nothing to deliver yet
                }

                sig = reassembled;
            }
            LogDlSignal(portName, ctx.Local.ToString(), ctx.Remote.ToString(), SignalName(sig));
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
            onTimerExpiry: name =>
            {
                LogTimerExpiry(portName, ctx.Local.ToString(), ctx.Remote.ToString(), name, ctx.RC);
                sessionRef!.PostEvent(TimerExpiry(name));
            },
            sendSFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward: SendUpward,
            // Grant LM-SEIZE after the §6.7.1.2 acknowledge delay (T2). The
            // listener fronts a KISS modem (TCP / serial / loopback), so the
            // medium is contention-free from the session's point of view —
            // real channel access (CSMA persist/slottime) is the TNC's job,
            // and it buffers. The grant itself is mandatory: without it the
            // figc4.x delayed ack (Set Ack Pending + LM-SEIZE Request → RR on
            // LM-SEIZE Confirm) never flushes, so a session with no reply
            // data never acknowledges received I-frames and the peer retries
            // into link failure (#327). But granting IMMEDIATELY acks once
            // per received I-frame: t23 clears Ack-Pending when the RR
            // flushes, so the next in-sequence frame re-seizes — five
            // back-to-back I-frames cost five RR keyups, and on a half-duplex
            // channel that TX occupancy deafens the port to the peer's next
            // window, leaving the F=1 checkpoint answer to a later poll
            // carrying a stale V(R) the peer rolls back to (#385). So the
            // grant is deferred by the context's T2 (the v2.2 §6.7.1.2
            // acknowledge timer — the SDL figures themselves ack immediately
            // and never arm T2): the first in-sequence I-frame of a burst
            // arms T2 (t26 requests the seize only while no ack is pending),
            // follow-on frames just advance V(R), and the single confirm at
            // expiry emits ONE cumulative RR — Enquiry_Response reads
            // N(R):=V(R) at dispatch time. Any other N(R)-bearing emission in
            // the meantime (a piggybacking I-frame, an REJ, an enquiry/poll
            // response) supersedes the pending ack: its action chain runs
            // Clear Acknowledge Pending, which also cancels the armed T2 (see
            // ActionDispatcher), and a confirm that fires anyway lands on the
            // no-ack-pending branch (LM-RELEASE only — no stale RR can ever
            // reach the wire). T2 ≤ 0 restores the legacy immediate grant
            // (ack-per-frame). Posts are deferred by PostEvent's
            // run-to-completion queue; bounded: the confirm path only emits
            // LM-RELEASE, never a re-seize.
            sendLinkMux: signal =>
            {
                if (signal is not LinkMultiplexerSeizeRequest)
                {
                    return;
                }

                if (ctx.T2 > TimeSpan.Zero)
                {
                    // Never re-arm a running delay: the ack must fire T2 after
                    // the FIRST unacknowledged frame, not slip later.
                    if (!scheduler.IsRunning(Ax25TimerNames.T2))
                    {
                        scheduler.Arm(Ax25TimerNames.T2, ctx.T2, () => sessionRef!.PostEvent(new LmSeizeConfirm()));
                    }
                }
                else
                {
                    sessionRef!.PostEvent(new LmSeizeConfirm());
                }
            },
            // The data-link figc4.6 UA-received path raises MDL-NEGOTIATE Request
            // after a successful v2.2 connect; hand it to the MDL driver to open
            // the XID exchange. (Other internal signals — push_I_frame_queue — are
            // queue-management that mutate ctx directly; nothing to do here.)
            sendInternal: sig => { if (sig is MdlNegotiateRequestSignal) { mdl.Negotiate(); } },
            subroutines: new DefaultSubroutineRegistry())
        {
            // Per-port timer overrides. InitialSrt seeds the establishment path's
            // `SRT := Initial Default; T1V := 2 * SRT` so a configured T1V actually
            // reaches the session's T1 timer (packet-net/packet.net#292) — without it,
            // the SDL resets T1V to 2×3000 ms on every connect. T3 arms the
            // inactive-link timer. The live T2 (the §6.7.1.2 acknowledge delay) is
            // read from ctx.T2 by the sendLinkMux grant above; the dispatcher copies
            // below only guard against an establishment-verb clobber (#292 class).
            InitialSrt = sp.T1V is { } t1vSeed ? t1vSeed / 2 : ActionDispatcher.DefaultInitialSrt,
            // Seed the establishment path's `N2 := 10` so a configured N2 survives the
            // SABM/SABME connect that would otherwise reset it to the spec default —
            // the same clobber class as InitialSrt above (#292). Without this the
            // listener's (N2+1)·T1V connect backstop is always the 66 s spec maximum.
            InitialN2 = sp.N2 ?? ActionDispatcher.DefaultInitialN2,
            // Seed the establishment-path `T2 := 3000` and (mod-8) `k := 8` link-param
            // verbs from the configured values so they survive a connect that runs
            // Set_Version — the same #292/#300 clobber class. These verbs are inert on
            // the connect path in the current SDL (Set_Version isn't invoked there, so
            // the BuildSession ctx seeds already survive), but seeding them here keeps
            // every establishment-init verb consistent and forecloses a re-introduced
            // clobber if an upstream SDL revision puts Set_Version back on the path.
            InitialT2 = sp.T2 ?? ActionDispatcher.DefaultInitialT2,
            InitialK = sp.K ?? ActionDispatcher.DefaultInitialK,
            T3Duration = sp.T3 ?? ActionDispatcher.DefaultT3,
            T2Duration = sp.T2 ?? ActionDispatcher.DefaultT2,
        };

        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: DefaultTransitionMap,
            initialState: "Disconnected");
        sessionRef = session;

        session.TransitionFired += (_, t) =>
            LogTransition(portName, ctx.Local.ToString(), ctx.Remote.ToString(),
                t.On.ToString(), t.From, t.Next);

        return new CachedSession
        {
            Session = session,
            Scheduler = scheduler,
            Signals = signals,
            Mdl = mdl,
            Segmentation = segmentation,
        };
    }

    private void AddToCache(SessionKey key, CachedSession built)
    {
        lock (cacheGate)
        {
            sessions[key] = built;
            UpdateLruLocked(key);
            EvictExcessLocked();
        }
    }

    private void TouchLru(SessionKey key)
    {
        lock (cacheGate)
        {
            UpdateLruLocked(key);
        }
    }

    private void UpdateLruLocked(SessionKey key)
    {
        if (lruIndex.TryGetValue(key, out var node))
        {
            lruOrder.Remove(node);
        }
        var added = lruOrder.AddLast(key);
        lruIndex[key] = added;
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
                LogSessionEvicted(portName, evicted.Local.ToString(), evicted.Remote.ToString());
                cs.Scheduler.Dispose();
            }
        }
    }

    private void TraceFrame(Ax25Frame frame, FrameDirection direction)
    {
        var handler = FrameTraced;
        if (handler is null)
        {
            return;
        }

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

    // ─── Debug logging (EventId band 5200–5299: AX.25 session layer) ────

    [LoggerMessage(EventId = 5200, Level = LogLevel.Information, Message = "AX.25 [{Port}] listener started as {MyCall}")]
    private partial void LogStarted(string port, string myCall);

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "AX.25 [{Port}] listener stopped")]
    private partial void LogStopped(string port);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Debug, Message = "AX.25 [{Port}] connecting {Local} → {Remote} ({Version})")]
    private partial void LogConnecting(string port, string local, string remote, string version);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} → {Remote}: pre-connect XID probe (SREJ offer)")]
    private partial void LogPreConnectXid(string port, string local, string remote);

    [LoggerMessage(EventId = 5204, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} → {Remote}: XID {Outcome} — {Detail}")]
    private partial void LogXidOutcome(string port, string local, string remote, string outcome, string detail);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Debug, Message = "AX.25 [{Port}] connected {Local} ↔ {Remote} ({Version})")]
    private partial void LogConnected(string port, string local, string remote, string version);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Warning, Message = "AX.25 [{Port}] connect {Local} → {Remote} timed out after {Seconds}s")]
    private partial void LogConnectTimeout(string port, string local, string remote, string seconds);

    [LoggerMessage(EventId = 5207, Level = LogLevel.Debug, Message = "AX.25 [{Port}] connect {Local} → {Remote} refused (DM / link reset)")]
    private partial void LogConnectRefused(string port, string local, string remote);

    [LoggerMessage(EventId = 5210, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Peer} → {Local}: {FrameType} received — accepting connection")]
    private partial void LogInboundAccept(string port, string peer, string local, string frameType);

    [LoggerMessage(EventId = 5211, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Peer} → {Local}: {FrameType} received — rejecting (DM)")]
    private partial void LogInboundReject(string port, string peer, string local, string frameType);

    [LoggerMessage(EventId = 5212, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} ↔ {Remote}: {Event} — {FromState} → {ToState}")]
    private partial void LogTransition(string port, string local, string remote, string @event, string fromState, string toState);

    [LoggerMessage(EventId = 5213, Level = LogLevel.Debug, Message = "AX.25 [{Port}] TX {Local} → {Remote}: {FrameDesc}")]
    private partial void LogTx(string port, string local, string remote, string frameDesc);

    [LoggerMessage(EventId = 5214, Level = LogLevel.Debug, Message = "AX.25 [{Port}] RX {Peer} → {Local}: {FrameDesc}")]
    private partial void LogRx(string port, string peer, string local, string frameDesc);

    [LoggerMessage(EventId = 5215, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} ↔ {Remote}: {Timer} expired (RC={RC})")]
    private partial void LogTimerExpiry(string port, string local, string remote, string timer, int rc);

    [LoggerMessage(EventId = 5216, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} ↔ {Remote}: ↑ {Signal}")]
    private partial void LogDlSignal(string port, string local, string remote, string signal);

    [LoggerMessage(EventId = 5217, Level = LogLevel.Debug, Message = "AX.25 [{Port}] session cache evicted {Local} ↔ {Remote}")]
    private partial void LogSessionEvicted(string port, string local, string remote);

    [LoggerMessage(EventId = 5218, Level = LogLevel.Debug, Message = "AX.25 [{Port}] CSMA: waited {Ms}ms for clear channel")]
    private partial void LogCsmaWait(string port, long ms);

    [LoggerMessage(EventId = 5219, Level = LogLevel.Debug, Message = "AX.25 [{Port}] TX {Source} → {Dest}: {FrameDesc}")]
    private partial void LogConnectionlessTx(string port, string source, string dest, string frameDesc);

    [LoggerMessage(EventId = 5220, Level = LogLevel.Debug, Message = "AX.25 [{Port}] {Local} ↔ {Remote}: XID {Direction} — {Detail}")]
    private partial void LogXid(string port, string local, string remote, string direction, string detail);

    private static readonly Dictionary<string, IReadOnlyList<TransitionSpec>> DefaultTransitionMap = new()
    {
        ["Disconnected"] = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"] = DataLink_Connected.Transitions,
        ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
    };

    /// <summary>The session-cache key: the (local, remote) callsign pair. Local is MyCall for
    /// ordinary sessions, or a registered alias / origination override (multi-callsign).</summary>
    private readonly record struct SessionKey(Callsign Local, Callsign Remote);

    private static string SignalName(DataLinkSignal sig) => sig switch
    {
        DataLinkConnectConfirm => "DL-CONNECT-confirm",
        DataLinkConnectIndication => "DL-CONNECT-indication",
        DataLinkDisconnectConfirm => "DL-DISCONNECT-confirm",
        DataLinkDisconnectIndication => "DL-DISCONNECT-indication",
        DataLinkDataIndication => "DL-DATA-indication",
        DataLinkUnitDataIndication => "DL-UNITDATA-indication",
        DataLinkErrorIndication err => $"DL-ERROR-indication({err.Code})",
        _ => sig.GetType().Name,
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        Ax25TimerNames.T1 => new T1Expiry(),
        Ax25TimerNames.T2 => new T2Expiry(),
        Ax25TimerNames.T3 => new T3Expiry(),
        _ => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
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
    /// Human-readable label for the modem/port this listener is attached to
    /// (e.g. "kiss-tcp:192.168.1.10:8001", "serial:/dev/ttyUSB0"). Included in
    /// every debug log line so the operator can tell which modem a message
    /// relates to. Defaults to "?" when unset.
    /// </summary>
    public string? PortName { get; init; }

    /// <summary>
    /// Override the session's initial T1V (acknowledgement timer). If
    /// <c>null</c>, sessions use the spec default (2 × initial SRT = 6 s).
    /// </summary>
    /// <remarks>
    /// The value seeds both <see cref="Ax25SessionContext.T1V"/> and, via the
    /// dispatcher's <see cref="ActionDispatcher.InitialSrt"/> (= T1V/2), the
    /// establishment path's <c>SRT := Initial Default; T1V := 2 * SRT</c> — so a
    /// configured T1V survives the SABM/SABME connect that would otherwise reset
    /// it to the spec default (packet-net/packet.net#292). figc4.7's
    /// <c>Select_T1_Value</c> still smooths the *running* value from round-trip
    /// samples once frames flow; this only sets the starting point.
    /// </remarks>
    public TimeSpan? T1V { get; init; }

    /// <summary>
    /// Override the session-context default T2 — the AX.25 v2.2 §6.7.1.2
    /// acknowledge (response-delay) timer. Received in-sequence I-frames
    /// coalesce into one cumulative RR sent T2 after the first
    /// unacknowledged frame, unless an N(R)-bearing transmission (a
    /// piggybacking I-frame, an REJ, a poll/enquiry response) supersedes
    /// it first. If <c>null</c>, sessions use the spec default (3 s).
    /// <see cref="TimeSpan.Zero"/> disables the delay entirely — every
    /// received I-frame is acknowledged immediately (ack-per-frame, the
    /// pre-#385 behaviour and what the SDL figures draw).
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
    /// Wire-parse options for this port's <em>inbound</em> frames. If
    /// <c>null</c>, the listener uses <see cref="Ax25ParseOptions.Lenient"/> —
    /// the historical behaviour of the parameterless decoder overloads. Set
    /// <see cref="Ax25ParseOptions.Strict"/> for spec-exact acceptance, or a
    /// peer preset (<see cref="Ax25ParseOptions.Bpq"/> etc.) to match a known
    /// neighbour. A frame the options reject is dropped before tracing or
    /// dispatch — the port is deaf to it, exactly as if it had failed CRC.
    /// The outbound construction path is unaffected (frames we build are
    /// always strict).
    /// </summary>
    public Ax25ParseOptions? ParseOptions { get; init; }

    /// <summary>
    /// SDL figure-defect / de-facto-interop quirks seeded onto each new
    /// session's <see cref="Ax25SessionContext.Quirks"/>. If <c>null</c>,
    /// sessions use <see cref="Ax25SessionQuirks.Default"/> (spec-correct —
    /// the existing behaviour). <see cref="Ax25SessionQuirks.StrictlyFaithful"/>
    /// runs the figures exactly as drawn, defects included — conformance
    /// study only, not for on-air use.
    /// </summary>
    public Ax25SessionQuirks? Quirks { get; init; }

    /// <summary>
    /// Prefer AX.25 v2.2 on every outbound <see cref="Ax25Listener.ConnectAsync(Callsign, CancellationToken)"/>:
    /// when <c>true</c> (default), a dial initiates an <b>extended (SABME / mod-128)</b>
    /// connect (the figc4.7 <c>Establish_Data_Link</c> subroutine branches on
    /// <c>mod_128</c> and emits SABME), with XID negotiating SREJ + window after the UA,
    /// and degrades cleanly to v2.0/SABM for peers that can't: a v2.2-incapable peer that
    /// answers our SABME with FRMR (LinBPQ) falls back via
    /// <see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>, and one that
    /// answers with DM (XRouter) falls back via
    /// <see cref="Ax25SessionQuirks.Ax25Spec48DmRejectionDegradesToV20"/> — so a dial never
    /// fails against a non-v2.2 peer. When <c>false</c>, a dial initiates a plain v2.0
    /// (SABM / mod-8) connect, the historical behaviour.
    /// </summary>
    /// <remarks>
    /// Affects the <em>outbound</em> dial only — the inbound answerer is untouched and
    /// still adopts whatever the peer offers (an inbound SABM runs <c>Set Version 2.0</c>,
    /// an inbound SABME runs <c>Set Version 2.2</c>, per figc4.1). A per-call override is
    /// available on <see cref="Ax25Listener.ConnectAsync(Callsign, Callsign, bool, CancellationToken)"/>.
    /// Not part of the <see cref="Ax25SessionParameters"/> live-reseed surface in the sense
    /// that it gates the dial, not a built session's context; it is still mirrored there so
    /// a reseed can change the default for future dials.
    /// </remarks>
    public bool PreferExtendedConnect { get; init; } = true;

    /// <summary>
    /// On a <b>mod-8 / v2.0</b> outbound dial (either a v2.0-preferred connect or
    /// the mod-8 link a v2.2 dial degraded to), run an <b>XID command/response
    /// exchange BEFORE the SABM</b> to negotiate Selective Reject (SREJ). When
    /// <c>true</c> (default), the dial first puts an XID command on the wire
    /// advertising SREJ + SREJ-multiframe at mod-8; if the peer answers with an XID
    /// response that also offers SREJ, the link runs SREJ recovery (selective
    /// retransmit) instead of go-back-N. If the peer does not answer XID (or rejects
    /// it), the dial proceeds to a plain SABM and the link is go-back-N — so this is
    /// always safe to leave on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <b>LinBPQ SREJ accommodation</b>, proven on the wire
    /// (<c>SrejXidViaNetsim</c>): LinBPQ does mod-8 SREJ but only when an XID
    /// <i>precedes</i> the SABM (its <c>L2Code.c</c> <c>ProcessXIDCommand</c> runs on
    /// the no-active-link path and sets <c>LINK-&gt;Ver2point2</c>; an XID on an
    /// already-established link is ignored). The AX.25 v2.2 figures instead negotiate
    /// XID <i>after</i> the connect (figc4.6 raises MDL-NEGOTIATE on the UA), which is
    /// what we do on the v2.2/SABME path and what direwolf does — but that post-connect
    /// XID never reaches BPQ's responder. So speaking SREJ to BPQ specifically needs
    /// the pre-SABM exchange; this knob enables it for the mod-8 dial.
    /// </para>
    /// <para>
    /// Reuses the per-session management-data-link driver
    /// (<see cref="Ax25ManagementDataLink"/>) and the existing inbound XID routing —
    /// it is the same XID exchange the post-UA path runs, simply triggered before the
    /// SABM. Affects the <em>outbound</em> dial only; the inbound answerer is
    /// untouched. Set <c>false</c> to restore the historical plain-SABM mod-8 dial
    /// (no pre-connect XID; the link is always go-back-N).
    /// </para>
    /// </remarks>
    public bool PreConnectXidNegotiatesSrej { get; init; } = true;

    /// <summary>
    /// Optional hook called once per newly-built session, before any
    /// events flow into it. Use to attach <c>onData</c> /
    /// <c>onDisconnect</c> handlers on the session's signal stream
    /// before the SDL processes the inbound SABM that triggered
    /// session creation.
    /// </summary>
    public Action<Ax25Session>? ConfigureSession { get; init; }

    /// <summary>
    /// Optional carrier-sense (CSMA) source the listener consults before it keys the radio:
    /// while it reports the channel busy the transmission is held, and it keys up once the
    /// channel clears (or a bounded wait expires — fail-open; see <see cref="CarrierSenseGate"/>).
    /// This is the general medium-access seam — any source that can observe channel occupancy
    /// (a radio-control channel's hardware DCD via <c>Packet.Radio.RadioCarrierSense</c>, or a
    /// future KISS DCD extension) supplies one so the AX.25 stack itself defers a keyup while
    /// another station is transmitting.
    /// </summary>
    /// <remarks>
    /// <c>null</c> (the default) is the always-clear degenerate gate: transmissions are never
    /// deferred, so behaviour is byte-for-byte the same as before, and the SDL transition
    /// behaviour is untouched — only the <em>physical</em> keyup is deferred, and only when a
    /// source is present and the channel is genuinely busy. Radio-agnostic by construction —
    /// <see cref="ICarrierSense"/> is a neutral, dependency-free capability
    /// (<c>Packet.Ax25.Transport.Abstractions</c>), not a radio detail. First-class and
    /// ax25-ts-parity-tracked (OQ-012): it mirrors the TypeScript
    /// <c>Ax25ListenerOptions.carrierSense</c>. Construction-time wiring, not a
    /// <see cref="Ax25SessionParameters"/> live-reseed knob (the gate is built once, like
    /// <see cref="ConfigureSession"/>).
    /// </remarks>
    public ICarrierSense? CarrierSense { get; init; }

    /// <summary>
    /// TX-complete→T1: when <c>true</c>, every T1-arming frame (I-frame,
    /// P=1 enquiry, SABM/SABME/DISC) is transmitted in KISS ACKMODE and a
    /// still-running T1 is re-armed to (now + T1V) when the TNC's
    /// TX-completion echo reports the frame has actually cleared the air.
    /// Default <c>false</c> — T1 runs from enqueue, the historical behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDL figures start T1 the instant a frame is "sent", which behind a
    /// buffering TNC means <em>enqueued</em> — on a 1200-baud channel a k-deep
    /// window is many seconds of airtime that all counts against T1, so the
    /// timer must be sized for worst-case queue depth rather than for the
    /// response time it is meant to bound (a k=4 window of 256-byte frames is
    /// ~8.5 s of air against the 6 s default T1V — measured self-destructing in
    /// <c>tools/Packet.LinkBench</c> rung 1b/2). With this option on, T1
    /// effectively runs from the moment the frame finished transmitting:
    /// near-default T1V works at any window size, and <c>Select_T1</c>'s SRT
    /// smoothing samples genuine round trips instead of queue noise.
    /// </para>
    /// <para>
    /// Requires a transport that implements <see cref="ITxCompletionTransport"/>;
    /// on a transport without it the listener latches back to plain sends after one
    /// failed attempt. Construction-time only (not part of the
    /// <see cref="Ax25SessionParameters"/> live-reseed). The re-arm is atomic
    /// against the SDL stopping T1 (<see cref="ITimerScheduler.RearmIfRunning"/>),
    /// so an ack racing the echo can never resurrect a stopped watchdog.
    /// </para>
    /// </remarks>
    public bool RestartT1OnTxComplete { get; init; }
}

/// <summary>
/// The subset of <see cref="Ax25ListenerOptions"/> that a running
/// <see cref="Ax25Listener"/> can live-reseed via
/// <see cref="Ax25Listener.UpdateSessionParameters"/> — the per-session AX.25
/// timing / window / cache knobs that only ever affect a session at the moment
/// it is built, plus the port's compatibility knobs (<see cref="ParseOptions"/>,
/// read live per inbound frame, and <see cref="Quirks"/>, seeded at session
/// build like the timers). Identity (<see cref="Ax25ListenerOptions.MyCall"/>) and the
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

    /// <summary>T2 (§6.7.1.2 acknowledge-delay timer) override. <c>null</c> ⇒ spec
    /// default (3 s); <see cref="TimeSpan.Zero"/> ⇒ ack-per-frame (no delay).</summary>
    public TimeSpan? T2 { get; init; }

    /// <summary>T3 (inactive-link timer) override. <c>null</c> ⇒ dispatcher default (30 s).</summary>
    public TimeSpan? T3 { get; init; }

    /// <summary>N2 (max retries) override. <c>null</c> ⇒ spec default (10).</summary>
    public int? N2 { get; init; }

    /// <summary>k (send-window size) override. <c>null</c> ⇒ spec default (4 for mod-8).</summary>
    public int? K { get; init; }

    /// <summary>
    /// N1 (maximum information-field length, octets) seed for newly-built sessions —
    /// the PACLEN cap. <c>null</c> ⇒ the session-context default (256, the AX.25 v2.2
    /// default + XID-offered N1). Seeds <see cref="Ax25SessionContext.N1"/> at build
    /// time; XID negotiation may still LOWER it (the negotiator takes the min of the
    /// two ends' offered N1), and the segmenter/accept-bound read the live
    /// <c>context.N1</c>. Build-time only, like the timer/window knobs — a reseed
    /// changes the value for <em>future</em> sessions, never a live session's N1.
    /// </summary>
    /// <remarks>
    /// This is a node-host per-port config seed, not a parity-tracked listener flag — it
    /// lives on the live-reseed parameter record (not <see cref="Ax25ListenerOptions"/>),
    /// so a freshly-constructed listener carries it via a post-construction
    /// <see cref="Ax25Listener.UpdateSessionParameters"/> reseed (the node host does this).
    /// </remarks>
    public int? N1 { get; init; }

    /// <summary>LRU cap on cached per-peer sessions. Defaults to 64 (the <see cref="Ax25ListenerOptions"/> default).</summary>
    public int MaxCachedPeers { get; init; } = 64;

    /// <summary>
    /// Inbound wire-parse options. <c>null</c> ⇒ <see cref="Ax25ParseOptions.Lenient"/>.
    /// Unlike the timing knobs this is not seeded into a session at build time —
    /// the inbound pump reads the live value per frame, so a reseed takes effect
    /// on the very next frame off the modem (it gates what the port hears at all,
    /// not how an established session behaves).
    /// </summary>
    public Ax25ParseOptions? ParseOptions { get; init; }

    /// <summary>Session quirks seeded onto newly-built sessions. <c>null</c> ⇒
    /// <see cref="Ax25SessionQuirks.Default"/>. Like the timing knobs, existing
    /// sessions keep the quirks they were built with.</summary>
    public Ax25SessionQuirks? Quirks { get; init; }

    /// <summary>Prefer AX.25 v2.2 (SABME / mod-128) on outbound dials. Default <c>true</c>
    /// (mirrors the <see cref="Ax25ListenerOptions"/> default). Gates the dial — a
    /// reseed changes the default for <em>future</em> dials, not links already up.</summary>
    public bool PreferExtendedConnect { get; init; } = true;

    /// <summary>Run a pre-SABM XID exchange to negotiate SREJ on mod-8 dials (the LinBPQ
    /// SREJ accommodation). Default <c>true</c> (mirrors the <see cref="Ax25ListenerOptions"/>
    /// default). Gates the dial — a reseed changes the default for <em>future</em> dials.</summary>
    public bool PreConnectXidNegotiatesSrej { get; init; } = true;

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
            ParseOptions = options.ParseOptions,
            Quirks = options.Quirks,
            PreferExtendedConnect = options.PreferExtendedConnect,
            PreConnectXidNegotiatesSrej = options.PreConnectXidNegotiatesSrej,
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
