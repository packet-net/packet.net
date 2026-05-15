using System.Net.Sockets;
using System.Text;

namespace Packet.Agw;

/// <summary>
/// High-level AGW client. Dials an AGW server (LinBPQ, direwolf,
/// SoundModem, XRouter), registers callsigns, opens connected-mode
/// sessions, and pumps a keepalive to defeat idle disconnects.
/// </summary>
/// <remarks>
/// <para>
/// AGW (SV2AGW's PE+ protocol) is the canonical interop interface for
/// software AX.25 stacks. Direwolf and SoundModem both expose it over
/// TCP; LinBPQ exposes it from `AGWPORT=...`; XRouter optionally
/// exposes it via `AGWPORT=`. The wire format is documented in
/// <see cref="AgwFrame"/>.
/// </para>
/// <para>
/// One <see cref="AgwClient"/> instance corresponds to one TCP
/// connection. Multiple in-flight L2 sessions over that connection
/// are demuxed by callsign pair; each shows up as an
/// <see cref="AgwSession"/> via <see cref="OpenSessionAsync"/>.
/// </para>
/// <para>
/// Keepalive: BPQ's AGW listener closes idle client connections after
/// ~20s of no traffic. The client pumps an <c>'R'</c> (version) ping
/// every <see cref="KeepaliveInterval"/> by default to keep the
/// connection alive. Set the interval to <see cref="TimeSpan.Zero"/>
/// to disable.
/// </para>
/// </remarks>
public sealed class AgwClient : IAsyncDisposable
{
    private readonly AgwFrameStream framing;
    private readonly TcpClient? tcp;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Task dispatchLoop;
    private readonly Task? keepaliveLoop;
    private readonly Dictionary<(string from, string to, byte port), AgwSession> sessions = new();
    private readonly object sessionsLock = new();
    private readonly List<TaskCompletionSource<AgwFrame>> connectWaiters = new();
    private readonly object waitersLock = new();

    /// <summary>
    /// Default keepalive interval. BPQ closes idle AGW clients at
    /// ~20s; 15s gives a 5-second headroom for in-flight ack.
    /// Direwolf and SoundModem don't time out clients but pumping the
    /// ping anyway is harmless and surfaces a dead connection quickly.
    /// </summary>
    public static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(15);

    private AgwClient(AgwFrameStream framing, TcpClient? tcp, TimeSpan keepaliveInterval)
    {
        this.framing = framing;
        this.tcp = tcp;
        KeepaliveInterval = keepaliveInterval;
        dispatchLoop = Task.Run(() => RunDispatchLoopAsync(lifetimeCts.Token));
        if (keepaliveInterval > TimeSpan.Zero)
        {
            keepaliveLoop = Task.Run(() => RunKeepaliveLoopAsync(keepaliveInterval, lifetimeCts.Token));
        }
    }

    /// <summary>Keepalive interval in effect. <see cref="TimeSpan.Zero"/> if disabled.</summary>
    public TimeSpan KeepaliveInterval { get; }

    /// <summary>
    /// Dial an AGW server at <paramref name="host"/>:<paramref name="port"/>.
    /// Default ports: LinBPQ 8000, direwolf 8000, SoundModem 8000.
    /// </summary>
    public static async Task<AgwClient> ConnectAsync(
        string host,
        int port = 8000,
        TimeSpan? keepaliveInterval = null,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        var framing = new AgwFrameStream(tcp.GetStream(), ownsStream: true);
        return new AgwClient(framing, tcp, keepaliveInterval ?? DefaultKeepaliveInterval);
    }

    /// <summary>
    /// Wrap an existing <see cref="Stream"/> (e.g. a paired
    /// <see cref="System.IO.Pipelines.Pipe"/> in tests, or a TLS-
    /// wrapped socket for production). The caller owns the stream's
    /// lifetime — disposal of this client does NOT close it.
    /// </summary>
    public static AgwClient FromStream(Stream stream, TimeSpan? keepaliveInterval = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var framing = new AgwFrameStream(stream, ownsStream: false);
        return new AgwClient(framing, tcp: null, keepaliveInterval ?? DefaultKeepaliveInterval);
    }

    /// <summary>
    /// Send an <c>X</c> (register callsign) command. Some servers
    /// reply with a single-byte status (BPQ uses 0x01 = "registered",
    /// XRouter uses 0x00 = "already registered" with a different
    /// convention). We tolerate either — registration is considered
    /// successful if the reply frame arrives at all.
    /// </summary>
    public async Task RegisterCallsignAsync(string callsign, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(callsign);
        await framing.WriteAsync(new AgwFrame(
            Port: 0,
            Kind: AgwCommandKind.RegisterCallsign,
            Pid: 0,
            From: callsign,
            To: "",
            Data: ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);

        // BPQ and XRouter both reply with a single-byte status frame
        // (kind 'X'). Wait briefly so callers know whether the server
        // acknowledged the registration before they try to use it.
        await AwaitFirstFrameAsync(
            predicate: f => f.Kind == AgwCommandKind.RegisterCallsign && f.From == callsign,
            timeout: TimeSpan.FromSeconds(5),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Open a connected-mode session by sending <c>C</c> (SABM) to
    /// <paramref name="to"/>. The returned <see cref="AgwSession"/>
    /// is a <see cref="Stream"/> — write bytes to send I-frames,
    /// read bytes from the remote.
    /// </summary>
    /// <param name="from">Our callsign (must have been registered first).</param>
    /// <param name="to">Remote callsign.</param>
    /// <param name="radioPort">AGW port number (radio interface). Defaults to 0.</param>
    /// <param name="connectTimeout">How long to wait for the connect-ack frame ('C' response). Default 30s.</param>
    public async Task<AgwSession> OpenSessionAsync(
        string from,
        string to,
        byte radioPort = 0,
        TimeSpan? connectTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        var key = (from, to, radioPort);

        var session = new AgwSession(this, from, to, radioPort);
        lock (sessionsLock)
        {
            if (sessions.ContainsKey(key))
                throw new InvalidOperationException($"AGW session for {from}->{to} on port {radioPort} already exists.");
            sessions[key] = session;
        }

        await framing.WriteAsync(new AgwFrame(
            Port: radioPort,
            Kind: AgwCommandKind.Connect,
            Pid: 0,
            From: from,
            To: to,
            Data: ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);

        // Wait for the server's connect-ack 'C' frame. Some servers
        // include a "Connected to <callsign>" message in the body;
        // we don't require it — the frame arrival itself is the ack.
        var budget = connectTimeout ?? TimeSpan.FromSeconds(30);
        var ack = await AwaitFirstFrameAsync(
            predicate: f => f.Kind == AgwCommandKind.Connect
                         && f.Port == radioPort
                         && f.From == to
                         && f.To == from,
            timeout: budget,
            ct).ConfigureAwait(false);
        if (ack is null)
        {
            // Connect refused or timed out. Remove the session from the
            // table so a retry attempt doesn't collide.
            lock (sessionsLock) sessions.Remove(key);
            throw new TimeoutException($"AGW connect from {from} to {to} on port {radioPort} did not receive a 'C' ack within {budget}.");
        }

        return session;
    }

    /// <summary>
    /// Send a <c>G</c> (port-info) query. Reply is a 'G' frame with
    /// a body of the form <c>"&lt;count&gt;;Port1Desc;Port2Desc;..."</c>
    /// (NUL-padded ASCII). Returned strings are trimmed.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPortInfoAsync(CancellationToken ct = default)
    {
        await framing.WriteAsync(new AgwFrame(
            Port: 0,
            Kind: AgwCommandKind.AskPortInfo,
            Pid: 0,
            From: "",
            To: "",
            Data: ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);

        var reply = await AwaitFirstFrameAsync(
            predicate: f => f.Kind == AgwCommandKind.AskPortInfo,
            timeout: TimeSpan.FromSeconds(5),
            ct).ConfigureAwait(false);

        if (reply is null) return Array.Empty<string>();

        var text = Encoding.ASCII.GetString(reply.Data.Span).TrimEnd('\0', ';', '\r', '\n');
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<string>();
        // First field is count — skip if numeric. Some servers omit it.
        var start = int.TryParse(parts[0], out _) ? 1 : 0;
        return parts.Skip(start).Select(p => p.Trim()).ToList();
    }

    // ─── Internal frame plumbing for AgwSession ────────────────────

    internal async ValueTask WriteFrameAsync(AgwFrame frame, CancellationToken ct)
        => await framing.WriteAsync(frame, ct).ConfigureAwait(false);

    internal void RemoveSession(AgwSession session)
    {
        var key = (session.From, session.To, session.RadioPort);
        lock (sessionsLock) sessions.Remove(key);
    }

    private async Task RunDispatchLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in framing.Inbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Dispatch to the matching session if there is one
                // (data + disconnect frames come in addressed
                // from-the-remote, so the dictionary key swaps From/To).
                AgwSession? session = null;
                lock (sessionsLock)
                {
                    // Try both directions — different servers / frame
                    // kinds use different conventions for which side
                    // is From vs To.
                    if (sessions.TryGetValue((frame.To, frame.From, frame.Port), out session))
                    {
                        // Inbound frame for our session: server reports
                        // From=remote, To=us; our session key is
                        // From=us, To=remote.
                    }
                    else if (sessions.TryGetValue((frame.From, frame.To, frame.Port), out session))
                    {
                        // Outbound-echoed frame (some servers echo our
                        // own sends): From=us, To=remote.
                    }
                }

                // Notify generic waiters (RegisterCallsign / Connect
                // / GetPortInfo are waiting on first-matching-frame).
                List<TaskCompletionSource<AgwFrame>>? toComplete = null;
                lock (waitersLock)
                {
                    foreach (var w in connectWaiters)
                    {
                        if (w.Task.IsCompleted) continue;
                        (toComplete ??= new()).Add(w);
                    }
                }
                if (toComplete is not null)
                {
                    foreach (var w in toComplete) w.TrySetResult(frame);
                }

                if (session is not null)
                {
                    session.OnFrame(frame);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (Exception ex)
        {
            // Fail every outstanding session / waiter so callers don't
            // block forever on a dead connection.
            lock (waitersLock)
            {
                foreach (var w in connectWaiters) w.TrySetException(ex);
                connectWaiters.Clear();
            }
            lock (sessionsLock)
            {
                foreach (var s in sessions.Values) s.OnStreamFault(ex);
                sessions.Clear();
            }
        }
    }

    private async Task RunKeepaliveLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (framing.IsClosed) return;
                try
                {
                    await framing.WriteAsync(new AgwFrame(
                        Port: 0,
                        Kind: AgwCommandKind.AskVersion,
                        Pid: 0,
                        From: "",
                        To: "",
                        Data: ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Underlying socket dropped — let dispatch loop
                    // surface the error through the normal path.
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    private async Task<AgwFrame?> AwaitFirstFrameAsync(
        Func<AgwFrame, bool> predicate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        // Subscribe to the next batch of frames; the dispatch loop
        // delivers every frame to every waiter, and the waiter keeps
        // taking results until it finds one matching the predicate.
        // Auto-removes itself from the waiter list when it completes.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeCts.Token);
        linked.CancelAfter(timeout);
        while (!linked.IsCancellationRequested)
        {
            var tcs = new TaskCompletionSource<AgwFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waitersLock) connectWaiters.Add(tcs);
            try
            {
                using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    AgwFrame frame;
                    try { frame = await tcs.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }

                    if (predicate(frame)) return frame;
                }
            }
            finally
            {
                lock (waitersLock) connectWaiters.Remove(tcs);
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetimeCts.CancelAsync().ConfigureAwait(false);
        try { await dispatchLoop.ConfigureAwait(false); } catch { /* swallow */ }
        if (keepaliveLoop is not null)
        {
            try { await keepaliveLoop.ConfigureAwait(false); } catch { /* swallow */ }
        }
        await framing.DisposeAsync().ConfigureAwait(false);
        tcp?.Dispose();
        lifetimeCts.Dispose();
    }
}
