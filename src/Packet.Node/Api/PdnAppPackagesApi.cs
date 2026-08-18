using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Api;
using Packet.Node.Core.Applications.Catalog;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The app-packages management API (<c>docs/app-packages.md</c> § Surfaces): the admin
/// inventory of every discovered package + every inline <c>applications:</c> entry
/// (<c>GET /api/v1/apps/packages</c>), the enable/disable trust toggle (a config write of the
/// <c>apps:</c> override list through the same <see cref="IWritableConfigProvider"/> seam every
/// other config-write endpoint uses), and the managed-service restart action (driving
/// <see cref="IAppServiceSupervisor.RestartAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scopes.</b> Reading the inventory is <c>read</c>; every mutation is <c>admin</c> -
/// enabling a package is the owner's trust grant, a step above day-to-day <c>operate</c>
/// actions. The gates are no-ops when <c>management.auth.enabled</c> is off, like everywhere
/// else.
/// </para>
/// <para>
/// <b>The supervisor is optional by design.</b> These endpoints resolve
/// <see cref="IAppServiceSupervisor"/> via <see cref="IServiceProvider"/> as a nullable
/// service: the node composition root registers the real one, but a host without it (a
/// degraded boot, a test host that strips the registration) must still serve the inventory -
/// the API degrades honestly, reporting managed services as <c>Stopped</c> with a
/// "supervisor not running" detail, and restart answers 503.
/// </para>
/// <para>
/// <b>Enable/disable is package-scoped.</b> The toggle writes the <c>apps:</c> override list,
/// which only governs discovered packages (the contract's owner-state surface). An inline
/// <c>applications:</c> entry keeps its own <c>enabled:</c> flag in config - toggling it is a
/// config edit, not an override write - so its id answers 404 here (it matches neither a
/// discovered package nor an override entry).
/// </para>
/// </remarks>
public static class PdnAppPackagesApi
{
    /// <summary>
    /// Map the app-packages management endpoints under <c>/api/v1/apps/packages</c>. Called
    /// from the node composition root beside the app-gateway and before the SPA fallback (the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all regardless of order).
    /// </summary>
    public static void MapPdnAppPackagesApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/apps/packages");

        // The admin inventory: every discovered package (the catalog, broken entries
        // included) + every inline applications: entry. Read-gated like the other reads.
        group.MapGet("", (IConfigProvider config, IAppPackageCatalog catalog, IServiceProvider services) =>
        {
            var supervisor = services.GetService<IAppServiceSupervisor>();
            return Results.Ok(BuildInventory(config.Current, catalog, supervisor));
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // The trust toggle: upsert the package's apps: override entry and persist it through
        // the config-write seam (validate inside TryApply → 422 on a rejected candidate, the
        // same discipline as the ports lifecycle flip). Admin: enabling is the trust grant.
        group.MapPost("/{id}/enable",
            (string id, HttpContext ctx, IWritableConfigProvider cfg, IAppPackageCatalog catalog, IServiceProvider services, IAuditLog audit, TimeProvider clock) =>
            {
                audit.RecordRest(ctx, clock, "enable_app", id, "requested", "");
                return SetEnabled(id, enable: true, cfg, catalog, services);
            })
            .RequireAuthorization(PdnAuthPolicies.Admin);

        group.MapPost("/{id}/disable",
            (string id, HttpContext ctx, IWritableConfigProvider cfg, IAppPackageCatalog catalog, IServiceProvider services, IAuditLog audit, TimeProvider clock) =>
            {
                audit.RecordRest(ctx, clock, "disable_app", id, "requested", "");
                return SetEnabled(id, enable: false, cfg, catalog, services);
            })
            .RequireAuthorization(PdnAuthPolicies.Admin);

        // Set a discovered package's packet identity (docs/app-packages.md § Application packet
        // identity): the command-verb override, the callsign pin, and the opt-in NET/ROM advert.
        // These are the NODE's to set (they encode this node's identity + location), so they live
        // on the apps: override beside enabled — written through the same config-write seam, with
        // the same TryApply → 422 discipline. Admin: it changes what the node answers for on the
        // air. An inline applications: entry is config-authored — like enable/disable, it answers
        // 404 here (edit it through the full-config PUT).
        group.MapPut("/{id}/identity",
            (string id, AppIdentityRequest body, HttpContext ctx, IWritableConfigProvider cfg, IAppPackageCatalog catalog, IServiceProvider services, IAuditLog audit, TimeProvider clock) =>
            {
                audit.RecordRest(ctx, clock, "set_app_identity", id, "requested",
                    $"callsign={body.Callsign} command={body.Command} netromAlias={body.NetromAlias}");
                return SetIdentity(id, body, cfg, catalog, services);
            })
            .RequireAuthorization(PdnAuthPolicies.Admin);

        // Stop-then-start one managed service regardless of backoff state — the owner's way
        // out of Faulted. 503 when no supervisor is wired; 404 for an unknown id; 409 for a
        // service pdn does not manage (none/external) or anything the supervisor refuses.
        group.MapPost("/{id}/restart",
            async (string id, HttpContext ctx, IConfigProvider config, IAppPackageCatalog catalog, IServiceProvider services,
                IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            audit.RecordRest(ctx, clock, "restart_app", id, "requested", "");
            var supervisor = services.GetService<IAppServiceSupervisor>();
            if (supervisor is null)
            {
                return Results.Json(
                    new { error = "The app service supervisor is not running." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var package = FindPackage(catalog.Discover(config.Current), id);
            if (package is null)
            {
                return Results.NotFound();
            }
            if (package.Manifest?.Service is not { Managed: AppServiceManaged.Pdn })
            {
                return Results.Json(
                    new { error = $"App '{package.Id}' has no pdn-managed service to restart." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await supervisor.RestartAsync(package.Id, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The supervisor's own refusal (e.g. the package is disabled so the service
                // is not in its desired set). The unknown-id case was already a 404 above.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(ProjectById(config.Current, catalog, supervisor, package.Id));
        }).RequireAuthorization(PdnAuthPolicies.Admin);

        // Uninstall a catalog/upload-installed package. Admin + audited. Refuses an ENABLED app
        // (never delete files a running app needs — disable first) → 409; 404 for an id that is
        // not a discovered package. On a clean run it first strips any apps: override for the id
        // (so a reinstall starts fresh), then deletes exactly the installer-recorded payload +
        // marker via UninstallAsync. A marker-less, hand-sideloaded dir is refused by the
        // installer (409 with its reason) — pdn never deletes files it did not place.
        group.MapPost("/{id}/uninstall",
            async (string id, HttpContext ctx, IWritableConfigProvider cfg, IAppPackageCatalog catalog,
                IAppInstaller installer, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
            {
                audit.RecordRest(ctx, clock, "uninstall_app", id, "requested", "");

                var current = cfg.Current;
                var package = FindPackage(catalog.Discover(current), id);
                if (package is null)
                {
                    return Results.NotFound();
                }

                // Never delete a running app's files. The effective trust state (override or an
                // inline-enable) gates this — disable it first.
                if (package.Enabled)
                {
                    return Results.Json(
                        new { error = "Disable the app before uninstalling." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Strip a leftover apps: override for the id so a later reinstall starts fresh.
                // (Best-effort: if the write is rejected we still attempt the uninstall — a
                // dangling disabled override is harmless and surfaced as a config warning.)
                var existing = current.Apps.FirstOrDefault(a =>
                    string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var apps = current.Apps
                        .Where(a => !string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    cfg.TryApply(current with { Apps = apps }, out _);
                }

                var outcome = await installer.UninstallAsync(package.Id, ct).ConfigureAwait(false);
                return outcome.Ok
                    ? Results.Ok(outcome)
                    : Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status409Conflict);
            }).RequireAuthorization(PdnAuthPolicies.Admin);

        // Upload a .pdnapp (a tar.gz of a package dir, manifest at root). Admin + audited. The
        // operator uploading the bytes IS the trust (no sha pin); the installer's path-traversal
        // guard + size cap still apply. 200 on a clean stage, 422 on a failure (no manifest,
        // bad archive, …). The request body is bounded to the installer's default artifact cap.
        group.MapPost("/upload",
            async (HttpContext ctx, [FromForm] IFormFile file, IAppInstaller installer,
                IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
            {
                audit.RecordRest(ctx, clock, "upload_app", file.FileName, "requested", $"len={file.Length}");

                await using var stream = file.OpenReadStream();
                var outcome = await installer.InstallFromUploadAsync(stream, ct).ConfigureAwait(false);
                return outcome.Ok
                    ? Results.Ok(outcome)
                    : Results.UnprocessableEntity(new { ok = false, id = outcome.Id, error = outcome.Error });
            })
            .RequireAuthorization(PdnAuthPolicies.Admin)
            .DisableAntiforgery()
            // Bound the request to the installer's artifact cap (the default fetch limit) — a
            // .pdnapp can be tens of MB, well over Kestrel's 30 MB default. Both the raw body
            // limit and the multipart length limit have to be lifted in step.
            .WithMetadata(
                new RequestSizeLimitAttribute(UploadMaxBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = UploadMaxBytes });
    }

    /// <summary>The .pdnapp upload size cap — the same default the artifact fetcher enforces for
    /// catalog downloads, so the two install faces share one bound.</summary>
    private const long UploadMaxBytes = HttpArtifactFetcher.DefaultMaxBytes;

    /// <summary>Flip (or create) the <c>apps:</c> override for <paramref name="id"/> and
    /// persist it through the write seam. 404 when the id matches neither a discovered
    /// package nor an existing override; 409 when enabling a broken package.</summary>
    private static IResult SetEnabled(
        string id, bool enable, IWritableConfigProvider cfg, IAppPackageCatalog catalog, IServiceProvider services)
    {
        var current = cfg.Current;
        var package = FindPackage(catalog.Discover(current), id);
        var existing = current.Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (package is null && existing is null)
        {
            return Results.NotFound();
        }

        // A broken package never runs — refuse the trust grant and tell the owner why.
        // (Disable stays allowed: switching a broken package off is always safe.)
        if (enable && package?.Error is not null)
        {
            return Results.Json(new { error = package.Error }, statusCode: StatusCodes.Status409Conflict);
        }

        // Upsert: flip the existing override, or append a fresh one carrying only the switch
        // (the canonical id casing comes from the catalog when we create the entry).
        IReadOnlyList<AppOverrideConfig> apps = existing is not null
            ? [.. current.Apps.Select(a => ReferenceEquals(a, existing) ? a with { Enabled = enable } : a)]
            : [.. current.Apps, new AppOverrideConfig { Id = package!.Id, Enabled = enable }];

        // The same apply discipline as the ports lifecycle flip: TryApply validates the whole
        // candidate before anything is persisted or Current advances — a rejected flip leaves
        // the node exactly as it was and surfaces as a 422 ValidationProblem.
        if (!cfg.TryApply(current with { Apps = apps }, out var errors))
        {
            return Results.UnprocessableEntity(new ValidationProblem(errors));
        }

        var supervisor = services.GetService<IAppServiceSupervisor>();
        return Results.Ok(ProjectById(cfg.Current, catalog, supervisor, package?.Id ?? existing!.Id));
    }

    /// <summary>Upsert the packet-identity overrides (command verb, callsign pin, NET/ROM advert)
    /// onto <paramref name="id"/>'s <c>apps:</c> override and persist through the write seam. An
    /// absent field in the request CLEARS that override (so the owner can blank a pin and fall
    /// back to auto-assignment); pass the current value to keep it. 404 when the id matches
    /// neither a discovered package nor an existing override; 422 when the candidate is rejected
    /// (e.g. a callsign/alias collision the validator catches). A bare-empty NET/ROM (no alias)
    /// is normalised to no advert at all.</summary>
    private static IResult SetIdentity(
        string id, AppIdentityRequest body, IWritableConfigProvider cfg, IAppPackageCatalog catalog, IServiceProvider services)
    {
        var current = cfg.Current;
        var package = FindPackage(catalog.Discover(current), id);
        var existing = current.Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (package is null && existing is null)
        {
            return Results.NotFound();
        }

        var netrom = Trim(body.NetromAlias) is { } alias
            ? new AppNetromConfig { Alias = alias, Quality = body.NetromQuality }
            : null;   // no alias ⇒ no advert (off by default), dropping any stale quality.

        // Upsert: rewrite the identity fields on the existing override (keeping its enable +
        // environment), or append a fresh disabled override carrying only the identity.
        var canonicalId = package?.Id ?? existing!.Id;
        AppOverrideConfig updated = (existing ?? new AppOverrideConfig { Id = canonicalId }) with
        {
            Command = Trim(body.Command),
            Callsign = Trim(body.Callsign),
            Netrom = netrom,
        };

        IReadOnlyList<AppOverrideConfig> apps = existing is not null
            ? [.. current.Apps.Select(a => ReferenceEquals(a, existing) ? updated : a)]
            : [.. current.Apps, updated];

        if (!cfg.TryApply(current with { Apps = apps }, out var errors))
        {
            return Results.UnprocessableEntity(new ValidationProblem(errors));
        }

        var supervisor = services.GetService<IAppServiceSupervisor>();
        return Results.Ok(ProjectById(cfg.Current, catalog, supervisor, canonicalId));
    }

    /// <summary>Trim a request string to null — an absent or blank field clears the override.</summary>
    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The whole inventory: discovered packages (catalog order), then the inline
    /// <c>applications:</c> entries (config order), then the configured-but-not-installed
    /// <c>apps:</c> overrides. The node-resolved callsign map is computed once (the node is the
    /// callsign authority) and threaded into every row's projection.</summary>
    private static List<AppPackageEntry> BuildInventory(
        NodeConfig config, IAppPackageCatalog catalog, IAppServiceSupervisor? supervisor)
    {
        var discovered = catalog.Discover(config);
        var callsigns = AppCallsignResolver.Resolve(config, discovered);
        var entries = new List<AppPackageEntry>();
        foreach (var package in discovered)
        {
            entries.Add(ProjectPackage(package, supervisor, callsigns));
        }
        foreach (var inline in config.Applications)
        {
            entries.Add(ProjectInline(inline, callsigns));
        }

        // Config says these apps exist; no root holds their package. Surfacing them is the
        // whole point (#738 item 2): the panel used to say "No app packages" for a node with
        // four enabled-but-payload-less apps, so a lost payload looked like a healthy node.
        // They project as `installed: false` rows, keeping the owner's trust switch visible so
        // the app can be turned off (or the package reinstalled) from the screen that told them.
        foreach (var missing in AppPackageCatalog.ConfiguredButNotInstalled(config, discovered))
        {
            entries.Add(ProjectNotInstalled(missing));
        }
        return entries;
    }

    /// <summary>Re-project one package's inventory entry from a fresh catalog snapshot (the
    /// post-mutation response body). Total: an override whose package has vanished from disk
    /// projects a minimal placeholder rather than failing the response.</summary>
    private static AppPackageEntry ProjectById(
        NodeConfig config, IAppPackageCatalog catalog, IAppServiceSupervisor? supervisor, string id)
    {
        var discovered = catalog.Discover(config);
        var callsigns = AppCallsignResolver.Resolve(config, discovered);
        var package = FindPackage(discovered, id);
        if (package is not null)
        {
            return ProjectPackage(package, supervisor, callsigns);
        }

        // The override exists but the package is not on disk (installed later — a config
        // warning, not an error). Project the override's state with no manifest data, but still
        // surface the owner-set identity overrides (command/callsign pin/netrom) so the edit the
        // owner just made round-trips even before the package lands.
        var override_ = config.Apps.FirstOrDefault(a =>
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        var resolvedCall = callsigns.TryGetValue(id, out var rc) ? rc.Callsign.ToString() : null;
        return ProjectNotInstalled(override_ ?? new AppOverrideConfig { Id = id }, resolvedCall);
    }

    /// <summary>One configured-but-not-installed app: an <c>apps:</c> override naming a package
    /// no root holds. <c>installed: false</c> is the whole signal: there is no manifest, so
    /// there is no name, version, capability list or service to report, and the owner's
    /// identity overrides are surfaced verbatim so they round-trip if the package lands
    /// later.</summary>
    private static AppPackageEntry ProjectNotInstalled(AppOverrideConfig app, string? resolvedCallsign = null) =>
        new(Id: app.Id, Name: app.Id, Version: null, Description: null, Icon: null,
            Capabilities: [], Enabled: app.Enabled, Source: "package", Installed: false,
            Error: null, Service: "none", State: null, Pid: null, Detail: null, Forwards: [],
            Command: app.Command, Callsign: resolvedCallsign, PinnedCallsign: app.Callsign,
            NetromAlias: app.Netrom?.Alias, NetromQuality: app.Netrom?.Quality);

    private static AppPackageEntry ProjectPackage(
        DiscoveredAppPackage package, IAppServiceSupervisor? supervisor,
        IReadOnlyDictionary<string, AppCallsignResolver.ResolvedAppCallsign> callsigns)
    {
        var manifest = package.Manifest;
        var service = manifest?.Service switch
        {
            null => "none",
            { Managed: AppServiceManaged.External } => "external",
            _ => "managed",
        };

        string? state = null;
        int? pid = null;
        string? detail = null;
        if (service is "managed" or "external")
        {
            var status = supervisor?.Statuses.FirstOrDefault(s =>
                string.Equals(s.Id, package.Id, StringComparison.OrdinalIgnoreCase));
            if (status is not null)
            {
                state = status.State.ToString();
                pid = status.Pid;
                detail = status.Detail;
            }
            else if (service is "external")
            {
                // pdn never tracks an external daemon's health — the state IS "External".
                state = nameof(AppServiceState.External);
            }
            else if (supervisor is null)
            {
                state = nameof(AppServiceState.Stopped);
                detail = "supervisor not running";
            }
            else
            {
                // Supervisor present but no status yet (e.g. a disabled package's service).
                state = nameof(AppServiceState.Stopped);
            }
        }

        return new AppPackageEntry(
            Id: package.Id,
            Name: manifest?.Name ?? package.Id,
            Version: manifest?.Version,
            Description: manifest?.Description,
            Icon: manifest?.Icon,
            // Display-normalise capabilities (network → packet) for the trust prompt; the
            // manifest's raw spelling is accepted on input (back-compat alias).
            Capabilities: AppCapabilities.NormalizeAll(manifest?.Capabilities),
            Enabled: package.Enabled,
            Source: "package",
            Installed: true,
            Error: package.Error,
            Service: service,
            State: state,
            Pid: pid,
            Detail: detail,
            Forwards: [.. package.Forwards.Select(f => new AppForwardEntry(
                f.Listen, f.Target, f.Tls == ForwardTls.Raw ? "raw" : "terminate"))],
            // Packet identity: the effective command verb (owner override ?? manifest), the
            // node-resolved callsign (pin or auto-assigned, null when the app binds none), the
            // owner's literal pin (null when the callsign above was auto-assigned), and the
            // opt-in NET/ROM advert (null until the owner sets it).
            Command: package.EffectiveCommand,
            Callsign: callsigns.TryGetValue(package.Id, out var resolved) ? resolved.Callsign.ToString() : null,
            PinnedCallsign: package.Override?.Callsign,
            NetromAlias: package.Override?.Netrom?.Alias,
            NetromQuality: package.Override?.Netrom?.Quality);
    }

    private static AppPackageEntry ProjectInline(
        ApplicationConfig inline,
        IReadOnlyDictionary<string, AppCallsignResolver.ResolvedAppCallsign> callsigns) => new(
        Id: inline.Id,
        Name: inline.Ui?.Name ?? inline.Id,
        Version: null,
        Description: null,
        Icon: inline.Ui?.Icon,
        // Display-normalise capabilities (network → packet) — same trust-prompt rule as packages.
        Capabilities: AppCapabilities.NormalizeAll(inline.Capabilities),
        Enabled: inline.Enabled,
        Source: "inline",
        // An inline entry IS its own payload (an executable/socket named in config), so there
        // is no package to be missing, so always installed.
        Installed: true,
        Error: null,
        Service: "none",
        State: null,
        Pid: null,
        Detail: null,
        // Inline applications: entries have no forward: block (it lives only in a package
        // manifest); always empty.
        Forwards: [],
        // The inline app's own packet identity (it carries the same fields directly, being the
        // owner-authored analog of a BPQ APPLICATION line).
        Command: inline.Command,
        Callsign: callsigns.TryGetValue(inline.Id, out var inlineCall) ? inlineCall.Callsign.ToString() : null,
        PinnedCallsign: inline.Callsign,
        NetromAlias: inline.Netrom?.Alias,
        NetromQuality: inline.Netrom?.Quality);

    private static DiscoveredAppPackage? FindPackage(IReadOnlyList<DiscoveredAppPackage> packages, string id) =>
        packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>One inventory row (the <c>/api/v1/apps/packages</c> shape — camelCase on the
    /// wire). <c>Source</c> is <c>package</c>|<c>inline</c>; <c>Installed</c> is false only for
    /// a configured <c>apps:</c> override whose package no root holds (there is no manifest, so
    /// no name, version, capability list or service; #738 item 2); <c>Service</c> is
    /// <c>none</c>|<c>managed</c>|<c>external</c>; <c>State</c> is an
    /// <see cref="AppServiceState"/> name, or null when there is no service. <c>Forwards</c> is
    /// the manifest's declared tailnet port forwards (empty for inline entries / no
    /// <c>forward:</c> block) — a capability the owner sees in the enable confirm. The
    /// packet-identity fields (<c>docs/app-packages.md</c> § Application packet identity):
    /// <c>Command</c> is the effective node-prompt verb (owner override ?? manifest /
    /// inline), <c>Callsign</c> is the node-resolved on-air callsign (a pin or an
    /// auto-assigned <c>&lt;node-base&gt;-N</c>, null when the app binds none),
    /// <c>PinnedCallsign</c> is the owner's literal <c>callsign</c> pin (null when the
    /// resolved callsign was auto-assigned), <c>NetromAlias</c>/<c>NetromQuality</c> are the
    /// opt-in NET/ROM advertisement (null when the owner hasn't opted in).
    /// <para><c>Callsign</c> alone cannot drive an edit: it is the RESOLVED value either way,
    /// so a client re-sending it would pin an auto-assignment, and a client sending null (as
    /// the panel's identity editor did) would silently CLEAR a pin on any verb/alias edit,
    /// since <c>PUT /{id}/identity</c> is a full replace (#702 C043). <c>PinnedCallsign</c> is
    /// the field that round-trips: it is the configured text verbatim, including the bare
    /// <c>-N</c> SSID form and a pin the resolver rejected, and it survives the app being
    /// disabled, which drops it out of the resolver's map entirely.</para></summary>
    public sealed record AppPackageEntry(
        string Id,
        string Name,
        string? Version,
        string? Description,
        string? Icon,
        IReadOnlyList<string> Capabilities,
        bool Enabled,
        string Source,
        bool Installed,
        string? Error,
        string Service,
        string? State,
        int? Pid,
        string? Detail,
        IReadOnlyList<AppForwardEntry> Forwards,
        string? Command,
        string? Callsign,
        string? PinnedCallsign,
        string? NetromAlias,
        int? NetromQuality);

    /// <summary>One declared tailnet forward on the wire (camelCase): the tailnet-facing
    /// <c>listen</c> port, the app's loopback <c>target</c> (host:port), and <c>tls</c>
    /// (<c>terminate</c> | <c>raw</c>). See <c>docs/network-access.md</c> § App-declared port
    /// forwarding.</summary>
    public sealed record AppForwardEntry(int Listen, string Target, string Tls);

    /// <summary>The <c>PUT /{id}/identity</c> body (camelCase on the wire): the owner's
    /// packet-identity overrides for a discovered package. Every field is optional — an absent /
    /// blank field clears that override (a blank callsign falls back to node auto-assignment; a
    /// blank NET/ROM alias turns the advert off). <see cref="NetromQuality"/> only matters when
    /// <see cref="NetromAlias"/> is set. See <c>docs/app-packages.md</c> § Application packet
    /// identity.</summary>
    public sealed record AppIdentityRequest(
        string? Command,
        string? Callsign,
        string? NetromAlias,
        int? NetromQuality);
}
