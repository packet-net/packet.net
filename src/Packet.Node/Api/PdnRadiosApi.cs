using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;

namespace Packet.Node.Api;

/// <summary>
/// The radio-control read surface of the pdn node API: the per-port radio attachment status/health
/// (<c>GET /api/v1/radios</c>, <c>GET /api/v1/ports/{id}/radio</c>), a bus discovery scan
/// (<c>GET /api/v1/radios/scan</c>), and the split-station head-end fleet scan + adopt
/// (<c>GET /api/v1/radios/headends</c>, <c>POST /api/v1/radios/headends/{instanceId}/adopt</c>). The
/// status + scan endpoints are read-scoped; adopt is operate-scoped (it writes config). Auth gates
/// are no-ops when <c>management.auth.enabled</c> is off. The status endpoints project the live
/// <see cref="PortSupervisor"/> via <see cref="RadioReadModels"/> (no serial I/O on the request
/// path); the scan endpoints drive injected scanners, bounded + total.
/// </summary>
public static class PdnRadiosApi
{
    /// <summary>Map the radio endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRadiosApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        var operate = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Operate);
        // Admin gates the RF-emitting action (keyup pairing keys transmitters on-air) — matching every
        // other transmitting endpoint in the node (hail / tuning / doctor are all admin-scoped).
        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);

        // Every configured radio attachment (one row per port with a radio: block), attached or not.
        v1.MapGet("/radios", (NodeHostedService host, IConfigProvider config)
            => Results.Ok(RadioReadModels.All(host.Supervisor, config.Current)));

        // One port's radio status. 404 when the port id is unknown; a port with no radio block
        // returns attached:false (honest "no radio here", distinct from "no such port").
        v1.MapGet("/ports/{id}/radio", (string id, NodeHostedService host, IConfigProvider config)
            => RadioReadModels.ForPort(host.Supervisor, config.Current, id) is { } status
                ? Results.Ok(status)
                : Results.NotFound());

        // Bus scan: probe candidate serial ports for attached radios, keyed by CCDI serial (the
        // stable identity) with the /dev/serial/by-id symlink where unambiguous. Read-scope, but it
        // opens serial ports transiently — the scanner keeps it bounded (timeout) + single-flight.
        // The scanner is unregistered only in a stripped embedder; then an empty array is honest.
        v1.MapGet("/radios/scan", async ([FromServices] IRadioScanner? scanner, CancellationToken ct) =>
        {
            IReadOnlyList<RadioScanResult> results = scanner is null
                ? Array.Empty<RadioScanResult>()
                : await scanner.ScanAsync(cancellationToken: ct).ConfigureAwait(false);
            return Results.Ok(results);
        });

        // Split-station head-end fleet scan/preview: enumerate every head-end instance (config ∪
        // mDNS), reach through each free device to identify + baud-lock it, and propose the matched
        // TNC↔radio pairs — plus any duplicate-instance-id conflicts. Read-scope: it opens remote
        // pipes transiently but mutates nothing. Absent scanner (stripped embedder) ⇒ empty scan.
        v1.MapGet("/radios/headends",
            async ([FromServices] IHeadEndRadioScanner? scanner, IConfigProvider config, CancellationToken ct) =>
        {
            if (scanner is null)
            {
                return Results.Ok(new HeadEndScan([], []));
            }
            var scan = await scanner.ScanAsync(config.Current, ct).ConfigureAwait(false);
            return Results.Ok(scan);
        });

        // Adopt a chosen head-end pairing: create ONE matched port (a nino-tnc-tcp transport + a
        // head-end-bound tait-ccdi radio on the same instance) through the SAME validate→preview→apply
        // seam the ports API uses — operator-confirmed, not silent auto-create. The head-end is
        // declared in config if not already present (discover mode unless an address is supplied).
        operate.MapPost("/radios/headends/{instanceId}/adopt",
            (string instanceId, HeadEndAdoptRequest body, HttpContext ctx, IWritableConfigProvider cfg, IAuditLog audit, TimeProvider clock) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.TncDeviceId) || string.IsNullOrWhiteSpace(body.RadioDeviceId))
            {
                return Results.BadRequest(new { error = "adopt requires both tncDeviceId and radioDeviceId." });
            }

            audit.RecordRest(ctx, clock, "adopt_headend", instanceId, "requested",
                $"tnc={body.TncDeviceId} radio={body.RadioDeviceId}");

            var candidate = HeadEndAdoption.BuildCandidate(cfg.Current, instanceId, body);
            return PdnPortsApi.ApplyCandidate(cfg, candidate);
        });

        // Keyup pairing: discover the PHYSICAL modem↔radio map on a head-end by briefly KEYING each
        // free NinoTNC (RF is emitted) and watching which co-located Tait reports its PTT — ground
        // truth that replaces the passive scan's co-location guess. Admin-scope (it transmits — the same
        // bar as hail/tuning/doctor) + an RF caveat on the response; never folded into the passive GET
        // scan. Absent pairer (stripped embedder) ⇒ an honest not-available result.
        admin.MapPost("/radios/headends/{instanceId}/pair-by-keyup",
            async (string instanceId, [FromServices] IHeadEndKeyupPairer? pairer, HttpContext ctx,
                IConfigProvider config, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (pairer is null)
            {
                return Results.Ok(new HeadEndKeyupResult(
                    instanceId, Reachable: false, Error: "keyup pairing is not available in this build",
                    Pairs: [], UnpairedTncs: [], UnpairedRadios: [], Ambiguous: [], HeadEndKeyupCaveat.Text));
            }

            audit.RecordRest(ctx, clock, "pair_by_keyup", instanceId, "requested",
                "RF: briefly keys each free NinoTNC to map its physical radio");

            var result = await pairer.PairByKeyupAsync(config.Current, instanceId, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });
    }
}
