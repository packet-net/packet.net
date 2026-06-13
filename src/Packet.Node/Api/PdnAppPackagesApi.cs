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
/// <b>Scopes.</b> Reading the inventory is <c>read</c>; every mutation is <c>admin</c> —
/// enabling a package is the owner's trust grant, a step above day-to-day <c>operate</c>
/// actions. The gates are no-ops when <c>management.auth.enabled</c> is off, like everywhere
/// else.
/// </para>
/// <para>
/// <b>The supervisor is optional by design.</b> These endpoints resolve
/// <see cref="IAppServiceSupervisor"/> via <see cref="IServiceProvider"/> as a nullable
/// service: the node composition root registers the real one, but a host without it (a
/// degraded boot, a test host that strips the registration) must still serve the inventory —
/// the API degrades honestly, reporting managed services as <c>Stopped</c> with a
/// "supervisor not running" detail, and restart answers 503.
/// </para>
/// <para>
/// <b>Enable/disable is package-scoped.</b> The toggle writes the <c>apps:</c> override list,
/// which only governs discovered packages (the contract's owner-state surface). An inline
/// <c>applications:</c> entry keeps its own <c>enabled:</c> flag in config — toggling it is a
/// config edit, not an override write — so its id answers 404 here (it matches neither a
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

    /// <summary>The whole inventory: discovered packages (catalog order), then the inline
    /// <c>applications:</c> entries (config order).</summary>
    private static List<AppPackageEntry> BuildInventory(
        NodeConfig config, IAppPackageCatalog catalog, IAppServiceSupervisor? supervisor)
    {
        var entries = new List<AppPackageEntry>();
        foreach (var package in catalog.Discover(config))
        {
            entries.Add(ProjectPackage(package, supervisor));
        }
        foreach (var inline in config.Applications)
        {
            entries.Add(ProjectInline(inline));
        }
        return entries;
    }

    /// <summary>Re-project one package's inventory entry from a fresh catalog snapshot (the
    /// post-mutation response body). Total: an override whose package has vanished from disk
    /// projects a minimal placeholder rather than failing the response.</summary>
    private static AppPackageEntry ProjectById(
        NodeConfig config, IAppPackageCatalog catalog, IAppServiceSupervisor? supervisor, string id)
    {
        var package = FindPackage(catalog.Discover(config), id);
        if (package is not null)
        {
            return ProjectPackage(package, supervisor);
        }

        // The override exists but the package is not on disk (installed later — a config
        // warning, not an error). Project the override's state with no manifest data.
        var enabled = config.Apps.FirstOrDefault(a =>
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Enabled ?? false;
        return new AppPackageEntry(
            Id: id, Name: id, Version: null, Description: null, Icon: null,
            Capabilities: [], Enabled: enabled, Source: "package",
            Error: null, Service: "none", State: null, Pid: null, Detail: null);
    }

    private static AppPackageEntry ProjectPackage(DiscoveredAppPackage package, IAppServiceSupervisor? supervisor)
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
            Capabilities: manifest?.Capabilities ?? [],
            Enabled: package.Enabled,
            Source: "package",
            Error: package.Error,
            Service: service,
            State: state,
            Pid: pid,
            Detail: detail);
    }

    private static AppPackageEntry ProjectInline(ApplicationConfig inline) => new(
        Id: inline.Id,
        Name: inline.Ui?.Name ?? inline.Id,
        Version: null,
        Description: null,
        Icon: inline.Ui?.Icon,
        Capabilities: inline.Capabilities,
        Enabled: inline.Enabled,
        Source: "inline",
        Error: null,
        Service: "none",
        State: null,
        Pid: null,
        Detail: null);

    private static DiscoveredAppPackage? FindPackage(IReadOnlyList<DiscoveredAppPackage> packages, string id) =>
        packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>One inventory row (the <c>/api/v1/apps/packages</c> shape — camelCase on the
    /// wire). <c>Source</c> is <c>package</c>|<c>inline</c>; <c>Service</c> is
    /// <c>none</c>|<c>managed</c>|<c>external</c>; <c>State</c> is an
    /// <see cref="AppServiceState"/> name, or null when there is no service.</summary>
    public sealed record AppPackageEntry(
        string Id,
        string Name,
        string? Version,
        string? Description,
        string? Icon,
        IReadOnlyList<string> Capabilities,
        bool Enabled,
        string Source,
        string? Error,
        string Service,
        string? State,
        int? Pid,
        string? Detail);
}
