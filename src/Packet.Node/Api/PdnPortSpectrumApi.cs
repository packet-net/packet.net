using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
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
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Maps the spectrum endpoint under <c>/api/v1</c>.</summary>
    public static void MapPdnPortSpectrumApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        read.MapGet("/ports/{id}/spectrum/events", SpectrumEventsAsync);
    }

    private sealed record SpectrumEvent(long Seq, double BinHz, string Bins);

    private static async Task SpectrumEventsAsync(
        string id, HttpContext ctx, NodeHostedService host, TimeProvider clock)
    {
        // The spectrum source is the soundmodem transport itself; the reconnect/pacing
        // decorators never wrap it (it is not a KISS-TCP kind), so the port's Transport
        // is the modem when the kind matches.
        if (host.Supervisor?.GetPort(id)?.Transport is not SoundModemFrameTransport modem)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = ctx.RequestAborted;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

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
            await WriteAsync(ctx, ": connected\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                var waitRead = lines.Reader.WaitToReadAsync(ct).AsTask();
                var heartbeat = Task.Delay(HeartbeatInterval, clock, ct);
                var done = await Task.WhenAny(waitRead, heartbeat);
                if (done == heartbeat)
                {
                    await WriteAsync(ctx, ": ping\n\n", ct);
                    continue;
                }

                if (!await waitRead)
                {
                    break;
                }

                while (lines.Reader.TryRead(out var line))
                {
                    var payload = new SpectrumEvent(
                        seq++, modem.SpectrumBinWidthHz, Convert.ToBase64String(line));
                    string json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
                    await WriteAsync(ctx, $"event: spectrum\ndata: {json}\n\n", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away — normal SSE teardown.
        }
        finally
        {
            modem.SpectrumLine -= OnLine;
        }
    }

    private static async Task WriteAsync(HttpContext ctx, string text, CancellationToken ct)
    {
        await ctx.Response.WriteAsync(text, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
