using System.Text.Json;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;

namespace Packet.Node.Api;

/// <summary>
/// The per-port spectrum feed for the modem-tuning waterfall:
/// <c>GET /api/v1/ports/{id}/spectrum/events</c> — an SSE stream of waterfall lines from a
/// <c>kind: soundmodem</c> port (404 for any other port kind, or a port that is not
/// running). Read-scoped, pure observation. Each <c>spectrum</c> event carries the
/// dB-scaled bins as base64 plus the bin width, at the modem's natural FFT cadence
/// (~3 lines/s, ~2.7 kB — comfortably inside the node's SSE-everywhere design).
/// </summary>
public static class PdnPortSpectrumApi
{
    /// <summary>Maps the spectrum endpoint under <c>/api/v1</c>.</summary>
    public static void MapPdnPortSpectrumApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        // The marker is what lets the browser's EventSource present its JWT as
        // ?access_token= (no header API); without it this feed 401s on an auth-on node.
        read.MapGet("/ports/{id}/spectrum/events", SpectrumEventsAsync)
            .WithMetadata(AcceptsQueryAccessToken.Instance);
    }

    private sealed record SpectrumEvent(long Seq, double BinHz, string Bins);

    private static async Task SpectrumEventsAsync(
        string id, HttpContext ctx, NodeHostedService host, TimeProvider clock)
    {
        // ModemTransport, not Transport: a radio-attached soundmodem port whose radio can read
        // RSSI wears an RssiTaggingTransport / InboundRadioTap wrapper that implements
        // IAx25Transport only and does not forward the concrete modem type - testing Transport
        // 404'd the waterfall on exactly those ports while /quality (which already used
        // ModemTransport) worked (review item C027, #694). Non-soundmodem or not-running ports
        // 404, the same shape /quality uses.
        if (host.Supervisor?.GetPort(id)?.ModemTransport is not SoundModemFrameTransport modem)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = ctx.RequestAborted;
        SseWriter.Begin(ctx);

        // Bounded hand-off from the receive-pump thread; drop-oldest so a slow browser
        // never stalls the modem (the NodeTelemetry fan-out discipline).
        var lines = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        void OnLine(ReadOnlyMemory<byte> line) => lines.Writer.TryWrite(line.ToArray());
        modem.SpectrumLine += OnLine;
        long seq = 0;

        try
        {
            await SseWriter.CommentAsync(ctx, "connected", ct);
            await SseWriter.PumpAsync(ctx, lines.Reader, clock, "spectrum", line =>
            {
                var payload = new SpectrumEvent(
                    seq++, modem.SpectrumBinWidthHz, Convert.ToBase64String(line));
                return JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
            }, ct);
        }
        finally
        {
            modem.SpectrumLine -= OnLine;
        }
    }
}
