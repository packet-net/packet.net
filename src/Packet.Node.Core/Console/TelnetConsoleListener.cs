using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Console;

/// <summary>
/// A plain TCP listener that accepts telnet dial-ins and runs a
/// <see cref="NodeCommandService"/> over each as a <see cref="TcpNodeConnection"/>.
/// This is the operator-local console — bound to loopback by default. It is NOT
/// an AX.25 transport (not an <c>IAx25Transport</c>); it is a second source of
/// <see cref="INodeConnection"/>s feeding the same console.
/// </summary>
/// <remarks>
/// The listener is restart-scoped: a hot-reload that changes the telnet
/// bind/port/enabled restarts just this listener (see the reconcile logic),
/// leaving AX.25 ports and their sessions untouched.
/// </remarks>
public sealed partial class TelnetConsoleListener : IAsyncDisposable
{
    private readonly TelnetConfig config;
    private readonly Func<INodeConnection, NodeCommandService> serviceFactory;
    private readonly ILogger<TelnetConsoleListener> logger;
    private readonly CancellationTokenSource lifecycle = new();
    private readonly object gate = new();
    private readonly HashSet<Task> sessions = new();

    private Socket? listenSocket;
    private Task? acceptLoop;
    private int started;
    private int disposed;

    public TelnetConsoleListener(
        TelnetConfig config,
        Func<INodeConnection, NodeCommandService> serviceFactory,
        ILogger<TelnetConsoleListener>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        this.logger = logger ?? NullLogger<TelnetConsoleListener>.Instance;
    }

    /// <summary>The endpoint this listener is bound to (for logging / tests).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    /// <summary>
    /// Bind and begin accepting. Idempotent — a second call is a no-op. Binding
    /// happens synchronously so a port clash surfaces here (the caller logs and
    /// continues; a telnet bind clash must not crash the node).
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        var ip = ParseBind(config.Bind);
        var endpoint = new IPEndPoint(ip, config.Port);
        var sock = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        sock.Bind(endpoint);
        sock.Listen(backlog: 16);
        listenSocket = sock;
        BoundEndpoint = (IPEndPoint)sock.LocalEndPoint!;
        LogListening(BoundEndpoint);

        acceptLoop = Task.Run(() => AcceptLoopAsync(lifecycle.Token), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var sock = listenSocket!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await sock.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                var sessionTask = Task.Run(() => HandleConnectionAsync(accepted, ct), CancellationToken.None);
                TrackSession(sessionTask);
            }
        }
        catch (ObjectDisposedException)
        {
            // listen socket disposed during shutdown — normal.
        }
    }

    private async Task HandleConnectionAsync(Socket accepted, CancellationToken ct)
    {
        var connection = new TcpNodeConnection(accepted);
        await using (connection.ConfigureAwait(false))
        {
            LogAccepted(connection.PeerId);
            try
            {
                await connection.NegotiateAsync(ct).ConfigureAwait(false);
                var service = serviceFactory(connection);
                await service.RunAsync(connection, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSessionError(ex, connection.PeerId);
            }
            finally
            {
                LogClosed(connection.PeerId);
            }
        }
    }

    private void TrackSession(Task t)
    {
        lock (gate) sessions.Add(t);
        _ = t.ContinueWith(done =>
        {
            lock (gate) sessions.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Stop accepting and wind down. The accept loop ends; in-flight
    /// sessions are cancelled via the lifecycle token.</summary>
    public async Task StopAsync()
    {
        if (Volatile.Read(ref started) == 0)
        {
            return;
        }
        await lifecycle.CancelAsync().ConfigureAwait(false);
        try { listenSocket?.Close(); } catch { /* ignore */ }

        if (acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        Task[] outstanding;
        lock (gate) outstanding = sessions.ToArray();
        try { await Task.WhenAll(outstanding).ConfigureAwait(false); }
        catch { /* sessions tearing down */ }
    }

    private static IPAddress ParseBind(string bind) =>
        IPAddress.TryParse(bind, out var ip) ? ip : IPAddress.Loopback;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await StopAsync().ConfigureAwait(false);
        lifecycle.Dispose();
        listenSocket?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telnet console listening on {Endpoint}.")]
    private partial void LogListening(IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telnet console session from {PeerId}.")]
    private partial void LogAccepted(string peerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telnet console session {PeerId} closed.")]
    private partial void LogClosed(string peerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telnet console session {PeerId} ended on an error.")]
    private partial void LogSessionError(Exception ex, string peerId);
}
