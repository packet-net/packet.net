using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;

namespace Packet.Node.Api;

/// <summary>
/// The per-port receive-quality read surface for the modem-tuning waterfall (#635):
/// <c>GET /api/v1/ports/{id}/quality</c> — a JSON snapshot of a <c>kind: soundmodem</c> port's
/// rolling per-frame FEC/CRC diagnostics (404 for any other port kind, or a port that is not
/// running). Read-scoped, pure observation. The body is the cumulative counters
/// (<c>cumulativeCorrectedBytes</c> is the early-warning number — persistently climbing means the
/// link is spending its FEC budget before frames start dropping) plus the most recent frames'
/// per-frame detail, newest first, so the waterfall screen can show a rolling quality strip.
/// </summary>
/// <remarks>
/// The value is deliberately <b>not</b> a bit-error rate: true BER is unobservable at a receiver.
/// <c>correctedBytes</c> per frame is an honest byte-error floor, and its <c>null</c> (an
/// unprotected HDLC framing carries no FEC count) is kept distinct from <c>0</c> (a clean IL2P
/// frame). Same in-process path as the metrics/log surfaces — it reads the transport's live meter
/// directly and never touches the daemon's opt-in KISS RxQuality frames.
/// </remarks>
public static class PdnPortQualityApi
{
    /// <summary>Maps the quality endpoint under <c>/api/v1</c>.</summary>
    public static void MapPdnPortQualityApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        read.MapGet("/ports/{id}/quality", (string id, HttpContext ctx, NodeHostedService host) =>
        {
            // ModemTransport, not Transport: a radio-attached soundmodem port wears an
            // RSSI-tagging wrapper that doesn't forward the concrete type (mirrors the metrics
            // collector). Non-soundmodem or not-running ports 404 — the same shape the spectrum
            // endpoint uses.
            if (host.Supervisor?.GetPort(id)?.ModemTransport is not SoundModemFrameTransport modem)
            {
                return Results.NotFound();
            }

            return Results.Ok(modem.QualitySnapshot());
        });
    }
}
