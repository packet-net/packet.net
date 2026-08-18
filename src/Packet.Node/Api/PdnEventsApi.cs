using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
/// for the client's lifetime - a periodic heartbeat comment (<c>: ping</c>)
/// keeps it warm through buffering proxies, and the loop tears down cleanly when
/// <see cref="HttpContext.RequestAborted"/> fires (the client navigated away or
/// the SPA closed the EventSource).
/// </para>
/// <para>
/// The envelope, the heartbeat/read race and the client-gone write policy come from the
/// shared <see cref="SseWriter"/>, one loop for all six of the node's SSE feeds (review item
/// C067, #694). No wall-clock (repo rule §2.7): the cadence rides the injected
/// <see cref="TimeProvider"/>, so it is fake-clock-controllable in tests.
/// </para>
/// </remarks>
public static class PdnEventsApi
{
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
            // (gated `read` below - the gate is a no-op when auth is disabled.)
            var ct = ctx.RequestAborted;

            SseWriter.Begin(ctx);
            using var sub = host.Telemetry.Subscribe(out var reader);

            // An initial comment flushes the headers + body so the client's onopen
            // fires promptly (before the first frame arrives).
            await SseWriter.CommentAsync(ctx, "connected", ct);

            // Web defaults camelCase the PascalCase MonitorEvent and emit single-line JSON
            // (no indentation): exactly one SSE data: line.
            await SseWriter.PumpAsync(ctx, reader, clock, "frame",
                evt => JsonSerializer.Serialize(evt, JsonSerializerOptions.Web), ct);
        }).RequireAuthorization(PdnAuthPolicies.Read)
          // A browser EventSource can't set an Authorization header - this marker is what
          // lets the JWT ride as ?access_token= on THIS route (see AcceptsQueryAccessToken).
          .WithMetadata(AcceptsQueryAccessToken.Instance);
    }
}
