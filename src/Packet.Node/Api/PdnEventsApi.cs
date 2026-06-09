using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The live side of the pdn node control API (Slice 3, step 1b): the
/// Server-Sent-Events feed the web monitor consumes via
/// <c>new EventSource("/api/v1/events")</c>. Each decoded
/// <see cref="Packet.Node.Core.Api.MonitorEvent"/> the
/// <see cref="NodeHostedService.Telemetry"/> fan-out produces is shipped as a
/// named <c>frame</c> SSE event whose <c>data:</c> line is the camelCase JSON
/// the client's <c>subscribeFrames</c> handler parses (see
/// <c>web/packetnet-ui/src/lib/api.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// Unauthenticated and read-only, like the rest of step-1/1b: the node binds
/// 127.0.0.1 by default and auth is a later step. The connection is held open
/// for the client's lifetime — a periodic heartbeat comment (<c>: ping</c>)
/// keeps it warm through buffering proxies, and the loop tears down cleanly when
/// <see cref="HttpContext.RequestAborted"/> fires (the client navigated away or
/// the SPA closed the EventSource).
/// </para>
/// <para>
/// No wall-clock here (repo rule §2.7): the heartbeat cadence comes from the
/// injected <see cref="TimeProvider"/> via <c>Task.Delay(ts, clock, ct)</c>, so
/// it is fake-clock-controllable in tests and free of <c>DateTime.Now</c>.
/// </para>
/// </remarks>
public static class PdnEventsApi
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Map the live SSE feed at <c>GET /api/v1/events</c>. Called from the node
    /// composition root after <see cref="PdnReadApi.MapPdnReadApi"/> and before the
    /// SPA fallback (the specific route wins over the <c>/api/{**rest}</c> catch-all
    /// regardless of order).
    /// </summary>
    public static void MapPdnEvents(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/events", async (HttpContext ctx, NodeHostedService host, TimeProvider clock) =>
        {
            var ct = ctx.RequestAborted;

            // SSE wire envelope: keep the stream un-buffered end to end so a frame
            // reaches the browser the instant it's broadcast, not on a proxy flush.
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            // nginx-style: tell the reverse proxy not to buffer this response.
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var sub = host.Telemetry.Subscribe(out var reader);

            // An initial comment flushes the headers + body so the client's onopen
            // fires promptly (before the first frame arrives).
            await WriteAsync(ctx, ": connected\n\n", ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Race a frame becoming readable against the heartbeat tick:
                    // whichever wins, we stay responsive to both.
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
                        // The telemetry channel completed (subscription disposed) —
                        // nothing more will arrive.
                        break;
                    }

                    while (reader.TryRead(out var evt))
                    {
                        // Web defaults camelCase the PascalCase MonitorEvent and emit
                        // single-line JSON (no indentation) — exactly one SSE data: line.
                        var json = JsonSerializer.Serialize(evt, JsonSerializerOptions.Web);
                        await WriteAsync(ctx, $"event: frame\ndata: {json}\n\n", ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away (RequestAborted). Normal SSE teardown — the
                // using-scoped subscription unsubscribes + completes the channel.
            }
        });
    }

    /// <summary>
    /// Write a UTF-8 SSE chunk and flush it immediately. A mid-write cancellation
    /// or <see cref="IOException"/> means the client vanished while we were writing
    /// — that's a normal disconnect, not a server fault, so it's swallowed rather
    /// than bubbling up as a 500.
    /// </summary>
    private static async Task WriteAsync(HttpContext ctx, string s, CancellationToken ct)
    {
        try
        {
            await ctx.Response.WriteAsync(s, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-write — expected.
        }
        catch (IOException)
        {
            // Broken pipe to a vanished client — expected.
        }
    }
}
