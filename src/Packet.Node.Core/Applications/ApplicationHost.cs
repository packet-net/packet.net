using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// Resolves and launches registered <see cref="ApplicationConfig"/> apps over a connected
/// session. The node console (application #0) calls this when a user types a verb it doesn't
/// recognise: if the verb matches a registered, enabled app, the host runs it over the same
/// session and returns control to the console when the app exits.
/// </summary>
public interface IApplicationHost
{
    /// <summary>Resolve an <b>enabled</b> application whose <see cref="ApplicationConfig.Command"/>
    /// equals <paramref name="verb"/> (case-insensitive, exact - no abbreviation), reading the
    /// live config so a hot edit applies to the next launch. Resolution is the <b>union</b> of
    /// the inline <c>applications:</c> list and the enabled, error-free app packages with a
    /// <c>session:</c> block (<c>docs/app-packages.md</c>) - inline first on a verb tie. Null
    /// if none matches.</summary>
    ApplicationConfig? Resolve(string verb);

    /// <summary>Run <paramref name="app"/> over <paramref name="session"/> until it finishes or
    /// the user drops, then return so the console can re-prompt. Total: a spawn/run failure is
    /// reported to the user and logged, never thrown (other than cancellation).</summary>
    Task RunAsync(ApplicationConfig app, INodeConnection session, NodeAppContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a bare node-prompt command verb to the resolved callsign of a <b>service</b> app
    /// (an enabled packet/service app, inline or package, with a <c>command</c> verb but no
    /// <c>session:</c> attachment). The console typing this verb then issues a loopback connect
    /// to that callsign - the same path <c>C &lt;callsign&gt;</c> takes. Returns null when the
    /// verb matches no service app (a session app is handled by <see cref="Resolve"/> instead, a
    /// built-in verb never reaches here). Reads live config so a hot edit applies immediately.
    /// </summary>
    Packet.Core.Callsign? ResolveServiceCommandCallsign(string verb);
}

/// <inheritdoc cref="IApplicationHost"/>
public sealed partial class ApplicationHost : IApplicationHost
{
    private readonly IConfigProvider config;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ApplicationHost> logger;
    // Optional app-package catalog (docs/app-packages.md). Null = inline applications only -
    // exactly the pre-package behaviour, so every existing caller/test is untouched.
    private readonly IAppPackageCatalog? catalog;

    public ApplicationHost(IConfigProvider config, ILoggerFactory? loggerFactory = null, IAppPackageCatalog? catalog = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.catalog = catalog;
        logger = this.loggerFactory.CreateLogger<ApplicationHost>();
    }

    /// <summary>
    /// The live view of which app callsigns are currently bound over RHP. Set by the host after the
    /// <c>PortSupervisor</c> is constructed (the supervisor IS the registry; the host builds it
    /// after this object - hence a settable seam, not a ctor arg). Null = no registry wired (older
    /// hosts / tests): the bare-verb resolver then returns the node-resolved callsign verbatim,
    /// exactly the pre-#476 behaviour. When present it lets the resolver reach a self-deriving app
    /// that bound a different SSID than its node-resolved <c>PDN_APP_CALLSIGN</c> (packet.net#476).
    /// </summary>
    public ILocalAppRegistry? LocalAppRegistry { get; set; }

    /// <inheritdoc/>
    public ApplicationConfig? Resolve(string verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            return null;
        }
        var wanted = verb.Trim();
        var current = config.Current;
        foreach (var app in current.Applications)
        {
            if (app.Enabled && !string.IsNullOrWhiteSpace(app.Command)
                && string.Equals(app.Command.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return app;
            }
        }
        return ResolvePackage(wanted, current);
    }

    /// <summary>The package half of the union: an enabled, error-free discovered package with a
    /// <c>session:</c> block whose effective command verb (owner override ?? manifest
    /// <c>packet.command</c>) matches. Mapped to the <see cref="ApplicationConfig"/> shape the
    /// existing run path already understands - executable/args resolved against the package dir,
    /// working dir = the app's state dir.</summary>
    private ApplicationConfig? ResolvePackage(string wanted, NodeConfig current)
    {
        if (catalog is null)
        {
            return null;
        }
        foreach (var pkg in catalog.Discover(current))
        {
            if (!pkg.Enabled || pkg.Error is not null || pkg.Manifest?.Session is not { } session)
            {
                continue;
            }
            var verb = pkg.EffectiveCommand;
            if (string.IsNullOrWhiteSpace(verb)
                || !string.Equals(verb.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // First use of the app's state dir - make it exist (it is the session's working
            // directory). A failure is logged and left for the spawn to surface as
            // "unavailable" through the total RunAsync path.
            try
            {
                Directory.CreateDirectory(pkg.StateDir);
            }
            catch (Exception ex)
            {
                LogStateDirFailed(ex, pkg.Id, pkg.StateDir);
            }

            return new ApplicationConfig
            {
                Id = pkg.Id,
                Command = verb.Trim(),
                Enabled = true,
                Kind = session.Kind,
                Executable = session.Command is null
                    ? null
                    : AppPackagePaths.ResolveFile(session.Command, pkg.PackageDir),
                SocketPath = session.SocketPath,
                Args = session.Args.Select(a => AppPackagePaths.ResolveFile(a, pkg.PackageDir)).ToArray(),
                WorkingDirectory = pkg.StateDir,
                Capabilities = pkg.Manifest.Capabilities,
                Ui = null,   // tiles are the gateway's concern - out of the session union's scope
            };
        }
        return null;
    }

    /// <inheritdoc/>
    public Packet.Core.Callsign? ResolveServiceCommandCallsign(string verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            return null;
        }
        var wanted = verb.Trim();
        var current = config.Current;
        var discovered = catalog?.Discover(current) ?? [];
        var callsigns = Packages.AppCallsignResolver.Resolve(current, discovered);

        // Inline apps are always session apps (they attach over the session via Resolve), so a
        // service-command verb is a package concern: an enabled, error-free package whose
        // effective command matches and which has NO session block (a session package is the
        // attachment path, handled by Resolve). Its daemon binds the resolved callsign over RHP;
        // typing the verb loopback-connects to it.
        foreach (var pkg in discovered)
        {
            if (!pkg.Enabled || pkg.Error is not null || pkg.Manifest?.Session is not null)
            {
                continue;
            }
            var command = pkg.EffectiveCommand;
            if (string.IsNullOrWhiteSpace(command)
                || !string.Equals(command.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (callsigns.TryGetValue(pkg.Id, out var resolved))
            {
                return BridgeToBoundCallsign(resolved.Callsign, callsigns);
            }
            return null;   // verb matched but the app has no resolvable callsign - nothing to dial.
        }
        return null;
    }

    /// <summary>
    /// Bridge the node-<i>resolved</i> service callsign to the one the app actually <c>bind</c>ed
    /// (packet.net#476). A <b>migrated</b> app binds exactly its <c>PDN_APP_CALLSIGN</c>, so the
    /// resolved callsign is live in the registry → return it unchanged (no regression). An
    /// <b>un-migrated, self-deriving</b> app instead bound some other SSID of the node base; its
    /// resolved callsign is then absent from the registry. In that case, if the registry holds
    /// exactly one "stray" binding on the same node base - a callsign that is not the node-resolved
    /// identity of any app, i.e. one the node didn't hand out - that stray is the self-deriving
    /// app, so dial it. Ambiguous (or no) stray ⇒ fall back to the resolved callsign: a best-effort
    /// that never guesses the wrong app, and matches the pre-#476 behaviour. With no registry wired
    /// (older hosts / tests) the resolved callsign is returned verbatim.
    /// </summary>
    private Packet.Core.Callsign BridgeToBoundCallsign(
        Packet.Core.Callsign resolved,
        IReadOnlyDictionary<string, Packages.AppCallsignResolver.ResolvedAppCallsign> callsigns)
    {
        var registry = LocalAppRegistry;
        if (registry is null || registry.IsRegistered(resolved))
        {
            return resolved;   // no registry, or the app bound exactly what the node resolved.
        }

        // The set of callsigns the node DID hand out (every app's node-resolved identity). A
        // registered callsign in this set belongs to some other (migrated) app - never the
        // self-deriving one we're looking for.
        var nodeAssigned = new HashSet<Packet.Core.Callsign>(callsigns.Values.Select(c => c.Callsign));

        var strays = registry.RegisteredCallsigns()
            .Where(c => !nodeAssigned.Contains(c)
                        && string.Equals(c.Base, resolved.Base, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        return strays.Count == 1 ? strays[0] : resolved;
    }

    /// <inheritdoc/>
    public async Task RunAsync(ApplicationConfig app, INodeConnection session, NodeAppContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        // CA1859: 'impl' is deliberately the INodeApplication abstraction, not the single
        // concrete type slice 1 happens to build - this is the platform seam, and later
        // ApplicationKinds (long-running socket, WASM) are dropped into the switch below. The
        // micro-perf of a concrete type is irrelevant against spawning a process per launch.
#pragma warning disable CA1859
        INodeApplication impl;
#pragma warning restore CA1859
        try
        {
            // The kind→implementation map. Slice 1 has one arm (out-of-process); later kinds
            // (long-running socket, WASM) are added here. Built per launch so config edits apply.
            impl = app.Kind switch
            {
                ApplicationKind.Process => new ExternalProcessApplication(app, loggerFactory.CreateLogger<ExternalProcessApplication>()),
                ApplicationKind.Socket => new SocketApplication(app, loggerFactory.CreateLogger<SocketApplication>()),
                _ => throw new NotSupportedException($"Unsupported application kind '{app.Kind}'."),
            };
        }
        catch (Exception ex)
        {
            LogBuildFailed(ex, app.Id);
            await WriteLineAsync(session, "That application is unavailable.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            LogLaunching(app.Id, context.Callsign);
            await impl.RunAsync(session, context, cancellationToken).ConfigureAwait(false);
        }
        catch (ApplicationStartException ex)
        {
            LogStartFailed(ex, app.Id);
            await WriteLineAsync(session, "That application is unavailable.", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // node/session shutting down - let the console loop unwind
        }
        catch (Exception ex)
        {
            LogRunFailed(ex, app.Id);
            await WriteLineAsync(session, "That application ended unexpectedly.", cancellationToken).ConfigureAwait(false);
        }
    }

    // Best-effort one-line write to the user, terminated per transport (CR for AX.25 /
    // NET-ROM, CR-LF for telnet) - matches the console's line discipline.
    private static async Task WriteLineAsync(INodeConnection connection, string text, CancellationToken ct)
    {
        var nl = connection.TransportKind == NodeTransportKind.Telnet ? "\r\n" : "\r";
        try
        {
            await connection.WriteAsync(Encoding.UTF8.GetBytes(text + nl), ct).ConfigureAwait(false);
        }
        catch
        {
            // The user is gone - nothing to report to.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching app '{Id}' for {Callsign}.")]
    private partial void LogLaunching(string id, string callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' could not be built.")]
    private partial void LogBuildFailed(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' failed to start.")]
    private partial void LogStartFailed(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' ended on an error.")]
    private partial void LogRunFailed(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App package '{Id}': could not create its state dir '{StateDir}'.")]
    private partial void LogStateDirFailed(Exception ex, string id, string stateDir);
}
