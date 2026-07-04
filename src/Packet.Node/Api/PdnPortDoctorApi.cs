using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Diagnostics;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The capability-doctor surface of the pdn node API — an operator's "Check radio setup" for one
/// port. Two forms, two scopes:
/// <list type="bullet">
///   <item><c>GET /api/v1/ports/{id}/doctor</c> — <b>safe</b>, <b>read</b>-scoped. Runs only the
///     non-transmitting probes (TNC identity/firmware/DIPs/running mode, radio identity, GETRSSI
///     availability). The transmitting probes are reported <c>unknown</c> "requires a brief
///     transmit". No RF is generated.</item>
///   <item><c>POST /api/v1/ports/{id}/doctor?interrupt=true</c> — <b>admin</b>-scoped and
///     <b>audited</b>. With <c>interrupt=true</c> it additionally runs the transmitting probes
///     (TXDELAY software-control, the SDM-enabled check, TNC↔radio pairing), which <b>briefly key
///     the transmitter and perturb TXDELAY</b>. Mirrors the other mutating/transmitting endpoints
///     (ping, port lifecycle): operate/admin scope + an audit entry.</item>
/// </list>
/// Both 404 an unknown or not-running port. A serial-KISS (non-NinoTNC) port reports the
/// TNC-diagnostic probes as "not a NinoTNC"; a port with no radio gets a <c>radio-attached</c>
/// "no radio attached" row. The projection is <see cref="PortDoctorRunner"/> (in Node.Core, so it
/// is unit-testable without Kestrel).
/// </summary>
public static class PdnPortDoctorApi
{
    /// <summary>Map the doctor endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnPortDoctorApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Safe form: read-scoped, never transmits.
        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        read.MapGet("/ports/{id}/doctor", async (
            string id,
            NodeHostedService host,
            IConfigProvider config,
            PortDoctorRunner runner,
            CancellationToken ct) =>
        {
            var running = host.Supervisor?.GetPort(id);
            if (running is null)
            {
                return Results.NotFound();
            }

            var report = await runner.RunAsync(
                id, running.NinoTnc, running.Radio, running.Config.Radio?.Kind,
                includeTransmitting: false, config.Current.Identity.Callsign,
                running.Config.Transport.Kind, ct).ConfigureAwait(false);
            return Results.Ok(report);
        });

        // Interrupt form: admin-scoped + audited; with interrupt=true it briefly TRANSMITS.
        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);
        admin.MapPost("/ports/{id}/doctor", async (
            string id,
            HttpContext ctx,
            NodeHostedService host,
            IConfigProvider config,
            PortDoctorRunner runner,
            IAuditLog audit,
            TimeProvider clock,
            CancellationToken ct,
            bool interrupt = false) =>
        {
            var running = host.Supervisor?.GetPort(id);
            if (running is null)
            {
                return Results.NotFound();
            }

            // The interrupt form keys the transmitter — audit who ran the full check on which port.
            audit.RecordRest(ctx, clock, "port_doctor", id, "requested", $"interrupt={interrupt}");

            var report = await runner.RunAsync(
                id, running.NinoTnc, running.Radio, running.Config.Radio?.Kind,
                includeTransmitting: interrupt, config.Current.Identity.Callsign,
                running.Config.Transport.Kind, ct).ConfigureAwait(false);
            return Results.Ok(report);
        });
    }
}
