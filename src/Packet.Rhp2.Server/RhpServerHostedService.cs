using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Rhp2.Server;

/// <summary>
/// Runs the RHPv2 listener per the node's <c>rhp:</c> config block (default-off), restarting
/// it when a hot-reload changes <c>enabled</c>/<c>bind</c>/<c>port</c>/<c>requireAuth</c> —
/// the same restart-scoped reconcile the telnet console uses. A bind failure is logged and
/// the node carries on (an RHP port clash must never crash the node).
/// </summary>
/// <remarks>
/// The wire's <c>auth</c> message is validated against the node's existing user store
/// (Argon2id, the same accounts the web panel uses) — RHP introduces no second credential
/// system. With <c>requireAuth</c> on and no users provisioned, every auth fails (no users ⇒
/// no access), matching the web panel's first-run posture.
/// </remarks>
public sealed partial class RhpServerHostedService : IHostedService, IAsyncDisposable
{
    private readonly IConfigProvider config;
    private readonly IRhpGateway gateway;
    private readonly IUserStore? users;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<RhpServerHostedService> logger;
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private RhpServer? server;
    private RhpConfig? running;          // the config snapshot the live server was built from
    private IDisposable? subscription;

    public RhpServerHostedService(
        IConfigProvider config,
        IRhpGateway gateway,
        IUserStore? users = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.users = users;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<RhpServerHostedService>();
    }

    /// <summary>The live server's bound endpoint (tests; null when disabled).</summary>
    public IPEndPoint? BoundEndpoint => server?.BoundEndpoint;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(config.Current.Rhp).ConfigureAwait(false);
        // Hot-reload: a changed rhp block restarts just this listener. Reconcile is
        // serialized so a burst of config changes can't interleave restarts.
        subscription = config.OnChange(next => _ = ReconcileAsync(next.Rhp));
    }

    private async Task ReconcileAsync(RhpConfig next)
    {
        await reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (running is not null && running == next)
            {
                return;   // record equality — nothing relevant changed
            }

            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                server = null;
                LogStopped();
            }
            running = next;

            if (!next.Enabled)
            {
                return;
            }

            if (!IPAddress.TryParse(next.Bind, out var bind))
            {
                LogBadBind(next.Bind);
                return;
            }

            var candidate = new RhpServer(
                new RhpServerOptions
                {
                    Bind = bind,
                    Port = next.Port,
                    RequireAuth = next.RequireAuth,
                    Authenticate = Validate,
                },
                gateway,
                loggerFactory.CreateLogger<RhpServer>());
            try
            {
                await candidate.StartAsync().ConfigureAwait(false);
                server = candidate;
            }
            catch (Exception ex)
            {
                // A bind clash must not crash the node — log and run without RHP.
                LogStartFailed(ex, next.Bind, next.Port);
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    // The wire's plaintext auth, checked against the node's Argon2id user store. No user
    // store wired (older hosts/tests) ⇒ every auth fails when auth is required.
    private bool Validate(string user, string pass)
        => users?.FindByUsername(user) is { } record && PasswordHasher.Verify(pass, record.PasswordHash);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        reconcileGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RHPv2 server stopped (config change).")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHPv2 server not started: rhp.bind '{Bind}' is not a valid IP address.")]
    private partial void LogBadBind(string bind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHPv2 server failed to bind {Bind}:{Port} — running without RHP.")]
    private partial void LogStartFailed(Exception ex, string bind, int port);
}
