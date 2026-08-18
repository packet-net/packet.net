using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// The <c>pdn-app/1</c> "next rung": runs a registered <see cref="ApplicationConfig"/> that is a
/// <b>long-running daemon</b> listening on a Unix-domain socket. The node opens a fresh
/// connection to that socket per connect and bridges the user's session over it
/// (<see cref="AppSessionBridge"/>) - so, unlike the spawn-per-connect process floor, the app can
/// hold <b>shared in-memory state across users</b> (a chat room, a live presence list) and push
/// unsolicited output. The owner runs the daemon (the node does not manage its lifecycle - it
/// only connects). See <c>docs/app-local-session-wire.md</c>.
/// </summary>
/// <remarks>
/// On connect the node writes the same connect header + bridges the session as line-oriented
/// UTF-8. Teardown closes the connection, which signals EOF to the daemon's accept handler (the
/// user left). A daemon that isn't listening surfaces as <see cref="ApplicationStartException"/>
/// (the host reports the app unavailable); the node itself never fails.
/// </remarks>
public sealed partial class SocketApplication : INodeApplication
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ApplicationConfig config;
    private readonly ILogger logger;

    public SocketApplication(ApplicationConfig config, ILogger? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.logger = logger ?? NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(config.SocketPath))
        {
            throw new ArgumentException("A socket application requires a socketPath.", nameof(config));
        }
    }

    /// <inheritdoc/>
    public async Task RunAsync(INodeConnection session, NodeAppContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        var path = config.SocketPath!;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;   // the node is shutting down - not an app failure
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
                {
                    LogConnectFailed(ex, config.Id, path);
                    var reason = ex is OperationCanceledException ? "connect timed out" : ex.Message;
                    throw new ApplicationStartException(config.Id, path, reason);
                }
            }

            LogConnected(config.Id, path, context.Callsign);
            var stream = new NetworkStream(socket, ownsSocket: false);
            await using (stream.ConfigureAwait(false))
            {
                // The "couldn't reach the app" failure is the connect above; the bridge itself is
                // total (it forwards any output even if the daemon drops us mid-header).
                await AppSessionBridge.RunAsync(
                    session, stream, stream, AppSessionBridge.BuildHeader(config.Id, context), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Closing the connection is what tells the daemon "this user is gone" (its recv → EOF).
            try { socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
            socket.Dispose();
            LogClosed(config.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "App '{Id}' connected to {Path} for {Callsign}.")]
    private partial void LogConnected(string id, string path, string callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' could not connect to {Path}.")]
    private partial void LogConnectFailed(Exception ex, string id, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "App '{Id}' connection closed.")]
    private partial void LogClosed(string id);
}
