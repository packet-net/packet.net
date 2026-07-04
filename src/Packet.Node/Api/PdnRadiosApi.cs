using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;

namespace Packet.Node.Api;

/// <summary>
/// The radio-control read surface of the pdn node API: the per-port radio attachment status/health
/// (<c>GET /api/v1/radios</c>, <c>GET /api/v1/ports/{id}/radio</c>) and a bus discovery scan
/// (<c>GET /api/v1/radios/scan</c>). All read-scoped; the gate is a no-op when
/// <c>management.auth.enabled</c> is off. The status endpoints project the live
/// <see cref="PortSupervisor"/> via <see cref="RadioReadModels"/> (no serial I/O on the request
/// path); the scan endpoint drives an injected <see cref="IRadioScanner"/>, which transiently opens
/// candidate serial ports but is bounded + single-flight.
/// </summary>
public static class PdnRadiosApi
{
    /// <summary>Map the radio endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRadiosApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);

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
    }
}
