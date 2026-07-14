using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Rigs;

namespace Packet.Node.Api;

/// <summary>
/// The rig-control (CAT) read surface of the pdn node API: per-port rig attachment status
/// (<c>GET /api/v1/rigs</c>, <c>GET /api/v1/ports/{id}/rig</c>) and the live poll-tick feed
/// (<c>GET /api/v1/rigs/events</c> — SSE, <c>event: rig</c>). All read-scoped; the gates are
/// no-ops when <c>management.auth.enabled</c> is off. The status endpoints project the live
/// <see cref="PortSupervisor"/> via <see cref="RigReadModels"/> (no rig I/O on the request
/// path); the SSE feed drains the <see cref="RigTelemetry"/> hub the per-port pollers publish
/// into. Read-only by design — tuning/mode/PTT mutation is a later, separately-gated slice.
/// </summary>
public static class PdnRigsApi
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Map the rig endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so
    /// the specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRigsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);

        // Every configured rig attachment (one row per port with a rig: block), attached or not.
        v1.MapGet("/rigs", (NodeHostedService host, IConfigProvider config)
            => Results.Ok(RigReadModels.All(host.Supervisor, config.Current)));

        // One port's rig status. 404 when the port id is unknown; a port with no rig block
        // returns attached:false (honest "no rig here", distinct from "no such port").
        v1.MapGet("/ports/{id}/rig", (string id, NodeHostedService host, IConfigProvider config)
            => RigReadModels.ForPort(host.Supervisor, config.Current, id) is { } status
                ? Results.Ok(status)
                : Results.NotFound());

        // The live feed: one `event: rig` per poll tick per attached rig, each carrying the full
        // RigStatus JSON (idle rigs tick at the poll cadence; a keyed transmitter ticks at the
        // meter cadence so SWR/power are live during a transmission). Same SSE envelope +
        // fake-clock heartbeat as /api/v1/events.
        v1.MapGet("/rigs/events", async (HttpContext ctx, NodeHostedService host, TimeProvider clock) =>
        {
            var ct = ctx.RequestAborted;

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var sub = host.RigTelemetry.Subscribe(out var reader);

            // Flush headers so the client's onopen fires promptly.
            await WriteAsync(ctx, ": connected\n\n", ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var waitRead = reader.WaitToReadAsync(ct).AsTask();
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

                    while (reader.TryRead(out var status))
                    {
                        var json = JsonSerializer.Serialize(status, JsonSerializerOptions.Web);
                        await WriteAsync(ctx, $"event: rig\ndata: {json}\n\n", ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client went away (RequestAborted) — normal SSE teardown.
            }
        });
    }

    private static async Task WriteAsync(HttpContext ctx, string s, CancellationToken ct)
    {
        try
        {
            await ctx.Response.WriteAsync(s, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Mid-write disconnect — the loop's cancellation check ends the stream.
        }
    }
}
