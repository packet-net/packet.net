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
/// Thin TUI-side facade over <see cref="Ax25Listener"/>. The listener owns
/// the inbound pump, per-peer session cache, address-filtering dispatch,
/// and the modem TX/RX trace stream. <see cref="SessionRunner"/> adds
/// only the TUI-specific UX:
/// <list type="bullet">
///   <item>"One connection at a time" policy via
///         <see cref="Ax25Listener.AcceptIncoming"/>.</item>
///   <item>Mapping <see cref="DataLinkSignal"/> events to
///         <see cref="LinkState"/> transitions for the status bar.</item>
///   <item>Surfacing inbound DL-DATA into the chat pane.</item>
///   <item>Welcome-banner emission on first DL-CONNECT-indication.</item>
/// </list>
/// </summary>
/// <remarks>
/// Compared with the pre-listener implementation, this drops the
/// hand-rolled inbound pump, per-SABM session-recreate logic, the
/// <c>ableToEstablish</c> closure plumbing, and the dual inbound /
/// outbound signal-drain loops. Per-peer SRT / T1V history is preserved
/// across reconnects — sessions sit idle in Disconnected rather than
/// being thrown away.
/// </remarks>
public sealed class SessionRunner : IDisposable
{
    private readonly Callsign myCall;
    private readonly Action<string> chatLog;
    private readonly Action<LinkState, Callsign?> onStateChange;
    private readonly Ax25Listener listener;

    private readonly object sessionLock = new();
    private Ax25Session? activeSession;
    private Callsign? remote;
    private LinkState state = LinkState.Disconnected;
    private bool welcomeSent;

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
    /// Construct a runner. The runner does not start its listener pump
    /// until <see cref="Start"/> is called.
    /// </summary>
    /// <param name="modem">Serial KISS modem the session talks through.</param>
    /// <param name="myCall">Our callsign; all inbound filtering checks against this.</param>
    /// <param name="chatLog">Sink for human-readable chat-pane lines.</param>
    /// <param name="onStateChange">Notified when the coarse link state changes.</param>
    public SessionRunner(KissSerialModem modem, Callsign myCall, Action<string> chatLog, Action<LinkState, Callsign?> onStateChange)
    {
        ArgumentNullException.ThrowIfNull(modem);
        this.myCall = myCall;
        this.chatLog = chatLog ?? throw new ArgumentNullException(nameof(chatLog));
        this.onStateChange = onStateChange ?? throw new ArgumentNullException(nameof(onStateChange));

        listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
            ConfigureSession = AttachSessionListeners,
        });
        listener.SessionAccepted += OnSessionAccepted;
    }

    /// <summary>
    /// Frame trace event — every TX/RX frame the listener observes.
    /// The TUI subscribes here to render the monitor pane.
    /// </summary>
    public event EventHandler<Ax25FrameEventArgs>? FrameTraced
    {
        add    => listener.FrameTraced += value;
        remove => listener.FrameTraced -= value;
    }

    /// <summary>
    /// Start the listener's inbound pump. Returns immediately; the pump
    /// runs in the background until <see cref="Dispose"/>.
    /// </summary>
    public Task Start(CancellationToken cancellationToken)
        => listener.StartAsync(cancellationToken);

    /// <summary>
    /// Open a session to <paramref name="target"/>, awaiting DL-CONNECT-confirm.
    /// </summary>
    public async Task<DataLinkSignal?> ConnectAsync(Callsign target, TimeSpan budget, CancellationToken cancellationToken)
    {
        lock (sessionLock)
        {
            if (state != LinkState.Disconnected)
            {
                throw new InvalidOperationException("a session is already active — disconnect first");
            }
            state = LinkState.Connecting;
            remote = target;
            welcomeSent = true;     // outbound: no welcome banner (we initiated).
        }
        onStateChange(LinkState.Connecting, target);

        // Block new inbound while we're connecting outbound — same
        // "one connection at a time" rule.
        listener.AcceptIncoming = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        try
        {
            var sess = await listener.ConnectAsync(target, cts.Token).ConfigureAwait(false);
            lock (sessionLock)
            {
                activeSession = sess;
                state = LinkState.Connected;
            }
            onStateChange(LinkState.Connected, target);
            return new DataLinkConnectConfirm();
        }
        catch (TimeoutException)
        {
            ResetState();
            return null;
        }
        catch (InvalidOperationException)
        {
            ResetState();
            return new DataLinkDisconnectIndication();
        }
        catch (OperationCanceledException)
        {
            ResetState();
            return null;
        }
    }

    /// <summary>
    /// Send the current line as one I-frame on the active session.
    /// </summary>
    public void SendData(ReadOnlyMemory<byte> bytes, byte pid = Packet.Ax25.Ax25Frame.PidNoLayer3)
    {
        Ax25Session? s;
        lock (sessionLock) s = activeSession;
        s?.PostEvent(new DlDataRequest(bytes, pid));
    }

    /// <summary>
    /// Post <see cref="DlDisconnectRequest"/> and wait briefly for
    /// the resulting <see cref="DataLinkDisconnectConfirm"/>. The
    /// session's <see cref="Ax25Session.DataLinkSignalEmitted"/> event
    /// (subscribed in <see cref="OnSessionAccepted"/>) reflects state
    /// back regardless of whether DisconnectAsync was awaited.
    /// </summary>
    public async Task<DataLinkSignal?> DisconnectAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        Ax25Session? s;
        Callsign? peer;
        lock (sessionLock)
        {
            s = activeSession;
            peer = remote;
            if (s is null) return null;
            state = LinkState.Disconnecting;
        }
        onStateChange(LinkState.Disconnecting, peer);

        var tcs = new TaskCompletionSource<DataLinkSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, DataLinkSignal sig)
        {
            if (sig is DataLinkDisconnectConfirm or DataLinkDisconnectIndication)
            {
                tcs.TrySetResult(sig);
            }
        }
        s.DataLinkSignalEmitted += Handler;
        try
        {
            s.PostEvent(new DlDisconnectRequest());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(budget);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(budget, cts.Token)).ConfigureAwait(false);
            if (done == tcs.Task)
            {
                return tcs.Task.Result;
            }
            return null;
        }
        finally
        {
            s.DataLinkSignalEmitted -= Handler;
            // Reset only after the disconnect completes (the
            // OnDataLinkSignalEmitted handler attached during
            // OnSessionAccepted also resets when it sees disconnect,
            // but doing it here avoids the gap if our handler races
            // theirs).
            ResetState();
        }
    }

    private void ResetState()
    {
        lock (sessionLock)
        {
            state = LinkState.Disconnected;
            remote = null;
            activeSession = null;
            welcomeSent = false;
        }
        listener.AcceptIncoming = true;
        onStateChange(LinkState.Disconnected, null);
    }

    private void AttachSessionListeners(Ax25Session sess)
    {
        // Subscribe to the session's signal stream before the SDL
        // processes the inbound SABM. Once SABM accepts, this handler
        // sees DL-CONNECT-indication and flips state via
        // OnSessionAccepted (the listener fires that after posting).
        // For DL-DATA-indication and DL-DISCONNECT-indication this
        // handler is the long-term route.
        sess.DataLinkSignalEmitted += OnSessionDataLinkSignal;
    }

    private void OnSessionDataLinkSignal(object? sender, DataLinkSignal sig)
    {
        if (sender is not Ax25Session sess) return;
        var peer = sess.Context.Remote;
        switch (sig)
        {
            case DataLinkDataIndication di:
                DeliverData(di, peer);
                break;

            case DataLinkDisconnectIndication:
            case DataLinkDisconnectConfirm:
                lock (sessionLock)
                {
                    if (!ReferenceEquals(activeSession, sess)) return;
                }
                chatLog($"*** {FormatCallsignDisplay(peer)} disconnected");
                ResetState();
                break;
        }
    }

    private void OnSessionAccepted(object? sender, Ax25SessionEventArgs e)
    {
        var sess = e.Session;
        var peer = sess.Context.Remote;

        lock (sessionLock)
        {
            // Skip if our outbound ConnectAsync already set state —
            // outbound resolves through its awaited path, not through
            // SessionAccepted's inbound flow.
            if (state == LinkState.Connected) return;
            if (state == LinkState.Connecting && remote is { } r && r.Equals(peer)) return;

            activeSession = sess;
            remote = peer;
            state = LinkState.Connected;
        }
        onStateChange(LinkState.Connected, peer);

        listener.AcceptIncoming = false;
        chatLog($"*** Incoming connection from {FormatCallsignDisplay(peer)}");

        // Welcome banner on the first inbound connect of a session.
        // Won't fire on subsequent reconnects from the same peer in
        // this run (the cached session persists welcomeSent).
        bool sendBanner;
        lock (sessionLock) { sendBanner = !welcomeSent; welcomeSent = true; }
        if (sendBanner)
        {
            var msg = System.Text.Encoding.ASCII.GetBytes(
                $"Packet.Term {AppInfo.Version} ready. Hello {FormatCallsignDisplay(peer)}.\r");
            SendData(msg);
        }
    }

    private void DeliverData(DataLinkDataIndication di, Callsign peer)
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
        chatLog($"{FormatCallsignDisplay(peer)}: {sb}");
    }

    private static string FormatCallsignDisplay(Callsign c)
        => c.Ssid == 0 ? c.Base : c.ToString();

    /// <inheritdoc/>
    public void Dispose()
    {
        try { listener.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch { /* swallowed */ }
    }
}
