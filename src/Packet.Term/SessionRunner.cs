using System.Collections.Concurrent;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Term;

/// <summary>
/// Coarse state of the data-link layer as Packet.Term presents it to the
/// user. Mirrors a useful subset of the SDL states without exposing the
/// full vocabulary — the TUI's status bar only needs three colours.
/// </summary>
public enum LinkState
{
    /// <summary>No session, idle.</summary>
    Disconnected,
    /// <summary>SABM in flight, awaiting UA.</summary>
    Connecting,
    /// <summary>Link established, exchanging I-frames.</summary>
    Connected,
    /// <summary>DISC in flight, awaiting UA.</summary>
    Disconnecting,
}

/// <summary>
/// Wraps <see cref="Ax25Session"/> + dispatcher + signal queue for one
/// session at a time. Owns the inbound pump (which reads frames off the
/// modem and dispatches them), the data-link signal observation loop
/// (which surfaces upper-layer events as TUI updates), and the
/// connect / disconnect lifecycle.
/// </summary>
/// <remarks>
/// One <see cref="SessionRunner"/> per modem connection. The runner
/// maintains exactly one <see cref="Ax25Session"/> at a time: outbound
/// connects build it before sending SABM; inbound connects rebuild it
/// when a SABM addressed to MYCALL arrives from an unknown peer (since
/// <see cref="Ax25SessionContext.Remote"/> is init-only).
/// </remarks>
public sealed class SessionRunner : IDisposable
{
    private readonly KissSerialModem modem;
    private readonly Callsign myCall;
    private readonly Action<string> chatLog;
    private readonly Action<LinkState, Callsign?> onStateChange;

    private readonly object sessionLock = new();
    private Ax25Session? session;
    private SystemTimerScheduler? scheduler;
    private ConcurrentQueue<DataLinkSignal>? signals;
    private Callsign? remote;
    private LinkState state = LinkState.Disconnected;
    private bool inSession;            // true between SABM/UA and DISC/UA
    private Task? signalLoop;
    private CancellationTokenSource? signalLoopCts;

    /// <summary>The active peer's callsign, or <c>null</c> when no session.</summary>
    public Callsign? Remote
    {
        get { lock (sessionLock) return remote; }
    }

    /// <summary>Current coarse link state.</summary>
    public LinkState State
    {
        get { lock (sessionLock) return state; }
    }

    /// <summary>
    /// Construct a runner. The runner does not start its pump until
    /// <see cref="Start"/> is called.
    /// </summary>
    /// <param name="modem">Serial KISS modem the session talks through.</param>
    /// <param name="myCall">Our callsign; all inbound filtering checks against this.</param>
    /// <param name="chatLog">Sink for human-readable chat-pane lines.</param>
    /// <param name="onStateChange">Notified when the coarse link state changes.</param>
    public SessionRunner(KissSerialModem modem, Callsign myCall, Action<string> chatLog, Action<LinkState, Callsign?> onStateChange)
    {
        this.modem = modem ?? throw new ArgumentNullException(nameof(modem));
        this.myCall = myCall;
        this.chatLog = chatLog ?? throw new ArgumentNullException(nameof(chatLog));
        this.onStateChange = onStateChange ?? throw new ArgumentNullException(nameof(onStateChange));
    }

    /// <summary>
    /// Start the inbound pump. Spawns a background task that reads frames
    /// off the modem, address-filters, and either creates a new inbound
    /// session (on SABM to MYCALL with no active link) or posts the
    /// classified event to the existing session.
    /// </summary>
    public Task Start(CancellationToken cancellationToken)
    {
        return Task.Run(() => InboundPump(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Build a fresh session targeting <paramref name="target"/>, then post
    /// <see cref="DlConnectRequest"/> to fire SABM. Returns the
    /// <see cref="DataLinkConnectConfirm"/> / disconnect indication / null
    /// (timeout) as the outcome.
    /// </summary>
    public async Task<DataLinkSignal?> ConnectAsync(Callsign target, TimeSpan budget, CancellationToken cancellationToken)
    {
        Ax25Session sessionLocal;
        ConcurrentQueue<DataLinkSignal> signalsLocal;
        lock (sessionLock)
        {
            if (inSession)
            {
                throw new InvalidOperationException("a session is already active — disconnect first");
            }
            (sessionLocal, signalsLocal) = BuildSession(target, ableToEstablish: () => !inSession);
            state = LinkState.Connecting;
        }
        onStateChange(LinkState.Connecting, target);

        sessionLocal.PostEvent(new DlConnectRequest());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                while (signalsLocal.TryDequeue(out var sig))
                {
                    switch (sig)
                    {
                        case DataLinkConnectConfirm:
                            lock (sessionLock)
                            {
                                inSession = true;
                                state = LinkState.Connected;
                            }
                            onStateChange(LinkState.Connected, target);
                            return sig;

                        case DataLinkDisconnectIndication:
                        case DataLinkDisconnectConfirm:
                            lock (sessionLock)
                            {
                                state = LinkState.Disconnected;
                                remote = null;
                            }
                            onStateChange(LinkState.Disconnected, null);
                            return sig;

                        case DataLinkDataIndication di when inSession:
                            DeliverData(di);
                            break;
                    }
                }
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through; budget expired.
        }

        // Timed out — tear down.
        lock (sessionLock)
        {
            state = LinkState.Disconnected;
            remote = null;
        }
        onStateChange(LinkState.Disconnected, null);
        return null;
    }

    /// <summary>
    /// Send the current line as one I-frame on the active session.
    /// </summary>
    public void SendData(ReadOnlyMemory<byte> bytes, byte pid = Ax25Frame.PidNoLayer3)
    {
        Ax25Session? s;
        lock (sessionLock) s = session;
        s?.PostEvent(new DlDataRequest(bytes, pid));
    }

    /// <summary>
    /// Post <see cref="DlDisconnectRequest"/> and await the resulting
    /// <see cref="DataLinkDisconnectConfirm"/>.
    /// </summary>
    public async Task<DataLinkSignal?> DisconnectAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        Ax25Session? s;
        ConcurrentQueue<DataLinkSignal>? q;
        Callsign? peer;
        lock (sessionLock)
        {
            s = session;
            q = signals;
            peer = remote;
            if (s is null || q is null)
            {
                return null;
            }
            state = LinkState.Disconnecting;
        }
        onStateChange(LinkState.Disconnecting, peer);

        s.PostEvent(new DlDisconnectRequest());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                while (q.TryDequeue(out var sig))
                {
                    if (sig is DataLinkDisconnectConfirm or DataLinkDisconnectIndication)
                    {
                        lock (sessionLock)
                        {
                            inSession = false;
                            state = LinkState.Disconnected;
                            remote = null;
                        }
                        onStateChange(LinkState.Disconnected, null);
                        return sig;
                    }
                }
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Budget exhausted — force the runner back to idle.
        }

        lock (sessionLock)
        {
            inSession = false;
            state = LinkState.Disconnected;
            remote = null;
        }
        onStateChange(LinkState.Disconnected, null);
        return null;
    }

    /// <summary>
    /// True if we should accept an inbound SABM right now — only when we
    /// don't already have an active session.
    /// </summary>
    private bool CanAcceptInbound()
    {
        lock (sessionLock) return !inSession;
    }

    private (Ax25Session, ConcurrentQueue<DataLinkSignal>) BuildSession(Callsign peer, Func<bool> ableToEstablish)
    {
        var sched = new SystemTimerScheduler(TimeProvider.System);
        var ctx = new Ax25SessionContext { Local = myCall, Remote = peer };
        var signalQ = new ConcurrentQueue<DataLinkSignal>();

        Ax25Session? sessionRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            try
            {
                _ = modem.SendDataAsync(bytes);
            }
            catch (Exception ex)
            {
                chatLog($"*** Modem error on TX: {ex.Message}");
            }
        }

        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    signalQ.Enqueue,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);

        // Default bindings but with our able_to_establish hook so a second
        // SABM while we're already in a session gets DM'd by the SDL.
        var defaultBindings = Ax25SessionBindings.CreateDefault(ctx, sched, () => sessionRef?.CurrentTrigger);
        var bindings = new Dictionary<string, Func<bool>>(defaultBindings, StringComparer.Ordinal)
        {
            ["able_to_establish"] = ableToEstablish,
        };
        var guards = new GuardEvaluator(bindings);

        var sessionLocal = new Ax25Session(
            ctx, sched, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = sessionLocal;

        lock (sessionLock)
        {
            session = sessionLocal;
            scheduler = sched;
            signals = signalQ;
            remote = peer;
        }

        return (sessionLocal, signalQ);
    }

    private void InboundPump(CancellationToken ct)
    {
        // We piggyback on the modem's FrameReceived event in addition to
        // ReadFramesAsync, because the latter also surfaces non-Data KISS
        // commands; this loop only cares about Data frames. We use the
        // async stream for cancellation safety.
        var stream = modem.ReadFramesAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    var moveNext = stream.MoveNextAsync();
                    moved = moveNext.AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    chatLog($"*** Modem error — link torn down: {ex.Message}");
                    return;
                }
                if (!moved) return;

                var kissFrame = stream.Current;
                if (kissFrame.Command != Packet.Kiss.KissCommand.Data) continue;
                if (!Ax25Frame.TryParse(kissFrame.Payload, out var parsed)) continue;

                HandleInboundFrame(parsed);
            }
        }
        finally
        {
            try { stream.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch { /* swallowed on shutdown */ }
        }
    }

    private void HandleInboundFrame(Ax25Frame parsed)
    {
        // Frames not addressed to us: monitor-only (already logged by the
        // Program's tap on the modem FrameReceived event); don't deliver
        // to any session.
        if (!parsed.Destination.Callsign.Equals(myCall))
        {
            return;
        }

        Ax25Session? activeSession;
        Callsign? activePeer;
        lock (sessionLock)
        {
            activeSession = session;
            activePeer = remote;
        }

        // Active session — only deliver frames whose source matches.
        if (activeSession is not null && activePeer is { } peer && parsed.Source.Callsign.Equals(peer))
        {
            activeSession.PostEvent(Ax25FrameClassifier.Classify(parsed));
            return;
        }

        // No active session (or source mismatches the active peer): only
        // SABM with us idle warrants new session creation.
        if (CanAcceptInbound() &&
            Ax25FrameClassifier.Classify(parsed) is SabmReceived)
        {
            var newPeer = parsed.Source.Callsign;
            chatLog($"*** Incoming connection from {FormatCallsignDisplay(newPeer)}");

            var (newSession, newSignals) = BuildSession(newPeer, ableToEstablish: () => !inSession);
            lock (sessionLock)
            {
                state = LinkState.Connecting;
            }
            onStateChange(LinkState.Connecting, newPeer);

            // Post the SABM into the brand-new session so it runs t14
            // (or whichever applies) and emits the UA.
            newSession.PostEvent(Ax25FrameClassifier.Classify(parsed));

            // Spin up a background loop to drain this session's signal
            // queue. ConnectAsync's loop only runs for outbound; inbound
            // needs its own observer.
            signalLoopCts?.Cancel();
            signalLoopCts = CancellationTokenSource.CreateLinkedTokenSource();
            signalLoop = Task.Run(() => DrainInboundSignals(newSignals, newPeer, signalLoopCts.Token));
        }
    }

    private async Task DrainInboundSignals(ConcurrentQueue<DataLinkSignal> q, Callsign peer, CancellationToken ct)
    {
        bool announced = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (q.TryDequeue(out var sig))
                {
                    switch (sig)
                    {
                        case DataLinkConnectIndication:
                            lock (sessionLock)
                            {
                                inSession = true;
                                state = LinkState.Connected;
                            }
                            onStateChange(LinkState.Connected, peer);
                            if (!announced)
                            {
                                announced = true;
                                chatLog($"*** Connected to {FormatCallsignDisplay(peer)}");
                                // Send the welcome banner the brief asks for.
                                var msg = System.Text.Encoding.ASCII.GetBytes(
                                    $"Packet.Term {AppInfo.Version} ready. Hello {FormatCallsignDisplay(peer)}.\r");
                                SendData(msg);
                            }
                            break;

                        case DataLinkDataIndication di:
                            DeliverData(di);
                            break;

                        case DataLinkDisconnectIndication:
                        case DataLinkDisconnectConfirm:
                            chatLog($"*** {FormatCallsignDisplay(peer)} disconnected");
                            lock (sessionLock)
                            {
                                inSession = false;
                                state = LinkState.Disconnected;
                                remote = null;
                            }
                            onStateChange(LinkState.Disconnected, null);
                            return;
                    }
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private void DeliverData(DataLinkDataIndication di)
    {
        var span = di.Info.Span;
        int end = span.Length;
        while (end > 0 && (span[end - 1] == 0x0D || span[end - 1] == 0x0A)) end--;
        var sb = new System.Text.StringBuilder(end);
        for (int i = 0; i < end; i++)
        {
            byte b = span[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        var peer = remote is { } r ? FormatCallsignDisplay(r) : "peer";
        chatLog($"{peer}: {sb}");
    }

    private static string FormatCallsignDisplay(Callsign c)
        => c.Ssid == 0 ? c.Base : c.ToString();

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        signalLoopCts?.Cancel();
        try { signalLoop?.Wait(TimeSpan.FromSeconds(1), CancellationToken.None); } catch { /* swallowed */ }
        signalLoopCts?.Dispose();
    }
}
