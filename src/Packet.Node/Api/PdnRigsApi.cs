using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Rigs;
using Packet.Rig;

namespace Packet.Node.Api;

/// <summary>
/// The rig-control (CAT) read surface of the pdn node API: per-port rig attachment status
/// (<c>GET /api/v1/rigs</c>, <c>GET /api/v1/ports/{id}/rig</c>) and the live poll-tick feed
/// (<c>GET /api/v1/rigs/events</c> — SSE, <c>event: rig</c>). All read-scoped; the gates are
/// no-ops when <c>management.auth.enabled</c> is off. The status endpoints project the live
/// <see cref="PortSupervisor"/> via <see cref="RigReadModels"/> (no rig I/O on the request
/// path); the SSE feed drains the <see cref="RigTelemetry"/> hub the per-port pollers publish
/// into. Mutation is deliberately narrow: operate-scoped set-frequency / set-mode (QSY — no RF
/// is emitted by a retune), audit-logged, capability-gated, and followed by a poller wake so the
/// SSE feed reflects the change immediately. PTT/keying is NOT exposed here — keying a
/// transmitter from the API is admin-scoped RF territory reserved for a deliberate future
/// tune-button design (see the keyup-pairing bar in PdnRadiosApi).
/// </summary>
public static class PdnRigsApi
{
    /// <summary>Set-frequency request body.</summary>
    public sealed record SetRigFrequencyRequest(long? FrequencyHz);

    /// <summary>Set-mode request body. <paramref name="PassbandHz"/> null lets the rig pick its
    /// default width for the mode.</summary>
    public sealed record SetRigModeRequest(string? Mode, int? PassbandHz);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Map the rig endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so
    /// the specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRigsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        var operate = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Operate);

        // Every configured rig attachment (one row per port with a rig: block), attached or not.
        v1.MapGet("/rigs", (NodeHostedService host, IConfigProvider config)
            => Results.Ok(RigReadModels.All(host.Supervisor, config.Current)));

        // One port's rig status. 404 when the port id is unknown; a port with no rig block
        // returns attached:false (honest "no rig here", distinct from "no such port").
        v1.MapGet("/ports/{id}/rig", (string id, NodeHostedService host, IConfigProvider config)
            => RigReadModels.ForPort(host.Supervisor, config.Current, id) is { } status
                ? Results.Ok(status)
                : Results.NotFound());

        // QSY: retune the attached rig's current VFO. Operate-scoped (a retune emits no RF —
        // the admin bar is for keying), audit-logged, capability-gated, run under the host's
        // exclusive gate so it can't race a port teardown, and followed by a poller wake so the
        // event: rig feed carries the new dial within a tick. Returns the read-back frequency.
        operate.MapPost("/ports/{id}/rig/frequency",
            async (string id, SetRigFrequencyRequest? body, HttpContext ctx, NodeHostedService host,
                IConfigProvider config, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (body?.FrequencyHz is not > 0)
            {
                return Results.BadRequest(new { error = "frequencyHz must be a positive Hz value." });
            }

            audit.RecordRest(ctx, clock, "rig_set_frequency", id, "requested", $"hz={body.FrequencyHz}");
            return await MutateRigAsync(host, config, id, RigCapabilities.FrequencySet, async rig =>
            {
                await rig.SetFrequencyAsync(body.FrequencyHz.Value, ct);
                var readBack = rig.Capabilities.HasFlag(RigCapabilities.FrequencyGet)
                    ? await rig.GetFrequencyAsync(ct)
                    : body.FrequencyHz.Value;
                return Results.Ok(new { frequencyHz = readBack });
            }, ct);
        });

        // Mode change on the current VFO. passbandHz null = the rig's default width for the
        // mode (the only cross-backend semantics); flrig rejects an explicit width upstream.
        operate.MapPost("/ports/{id}/rig/mode",
            async (string id, SetRigModeRequest? body, HttpContext ctx, NodeHostedService host,
                IConfigProvider config, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Mode))
            {
                return Results.BadRequest(new { error = "mode is required (a hamlib token like USB/PKTUSB, or the rig's native name)." });
            }
            RigMode mode;
            try
            {
                mode = RigMode.From(body.Mode);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            audit.RecordRest(ctx, clock, "rig_set_mode", id, "requested",
                $"mode={mode.Token} passband={body.PassbandHz?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}");
            return await MutateRigAsync(host, config, id, RigCapabilities.ModeSet, async rig =>
            {
                await rig.SetModeAsync(mode, body.PassbandHz, ct);
                if (rig.Capabilities.HasFlag(RigCapabilities.ModeGet))
                {
                    var state = await rig.GetModeAsync(ct);
                    return Results.Ok(new { mode = state.Mode.Token, passbandHz = state.PassbandHz });
                }
                return Results.Ok(new { mode = mode.Token, passbandHz = body.PassbandHz });
            }, ct);
        });

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

    /// <summary>The shared rig-mutation shape: resolve the port (404) and its attached rig
    /// (409 when configured-but-not-attached or the port has no rig), gate on the advertised
    /// capability (409), run the action under the host's exclusive gate, wake the poller, and
    /// map rig-level failures onto the API error shape (a daemon fault is 409 — transient, the
    /// backend re-dials; bad arguments are 400).</summary>
    private static async Task<IResult> MutateRigAsync(
        NodeHostedService host, IConfigProvider config, string portId,
        RigCapabilities required, Func<IRigControl, Task<IResult>> action, CancellationToken ct)
    {
        var port = config.Current.Ports.FirstOrDefault(p => string.Equals(p.Id, portId, StringComparison.Ordinal));
        if (port is null)
        {
            return Results.NotFound();
        }
        var running = host.Supervisor?.GetPort(portId);
        var rig = running?.Rig;
        if (port.Rig is null)
        {
            return Results.Conflict(new { error = $"port '{portId}' has no rig: block configured." });
        }
        if (rig is null)
        {
            return Results.Conflict(new { error = $"port '{portId}' has a rig configured but not attached (port down, or the daemon was unreachable at bring-up)." });
        }
        if (!rig.Capabilities.HasFlag(required))
        {
            return Results.Conflict(new { error = $"the attached rig does not advertise {required} — nothing was changed." });
        }

        try
        {
            var result = await host.RunExclusiveAsync(() => action(rig), ct);
            running!.RigStatus?.RequestRefresh();
            return result;
        }
        catch (RigException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (ObjectDisposedException)
        {
            return Results.Conflict(new { error = $"port '{portId}' tore down while the command was in flight." });
        }
        catch (NotSupportedException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
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
