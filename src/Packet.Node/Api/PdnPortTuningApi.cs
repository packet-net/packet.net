using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Tuning;
using Packet.Tune.Core;

namespace Packet.Node.Api;

/// <summary>Request body for <c>POST /api/v1/ports/{id}/tuning/session</c>.</summary>
/// <param name="Role">This port's role: <c>tuned</c> (transmits bursts; the operator turns the pot
/// here) or <c>meter</c> (measures a remote peer).</param>
/// <param name="PeerSdmId">The peer radio's 8-character SDM data identity.</param>
/// <param name="BurstFrames">Frames per measurement burst (optional; 1..50, default 5).</param>
public sealed record TuningStartRequest(string? Role, string? PeerSdmId, int? BurstFrames);

/// <summary>Request body for <c>POST /api/v1/ports/{id}/tuning/txdelay-min</c>.</summary>
/// <param name="Role">This port's role: <c>coordinator</c> (sweeps its OWN TXDELAY down — this end
/// transmits) or <c>meter</c> (purely passive decode counting for a remote coordinator).</param>
/// <param name="PeerSdmId">The peer radio's 8-character SDM data identity.</param>
/// <param name="StartMs">Coordinator: the TXDELAY to sweep down from, in ms (optional; default =
/// the port's configured <c>kiss.txDelay</c>, else 500).</param>
/// <param name="StepMs">Coordinator: the sweep decrement in ms (optional; default 40).</param>
/// <param name="MinMs">Coordinator: the sweep floor in ms (optional; default 20).</param>
/// <param name="ProbesPerStep">Separately-keyed probes per step (optional; 1..20, default 5).</param>
public sealed record TxDelayMinStartRequest(
    string? Role, string? PeerSdmId, int? StartMs, int? StepMs, int? MinMs, int? ProbesPerStep);

/// <summary>Request body for <c>POST /api/v1/ports/{id}/tuning/txdelay-min/apply</c>.</summary>
/// <param name="PeerSdmId">The peer radio's 8-character SDM data identity (the far end must be
/// running a txdelay-min <c>meter</c> session).</param>
/// <param name="TxDelayMs">The TXDELAY to apply and verify, in ms (typically the sweep's
/// recommendation).</param>
/// <param name="Probes">Verify probes (optional; 1..20, default 5).</param>
/// <param name="Persist">Persist a verified value into the port's <c>kiss.txDelay</c> config
/// (optional; default <c>true</c> — on the node the session-ending port rebuild re-applies the
/// configured KISS params, so an unpersisted apply is deliberately transient).</param>
/// <param name="FallbackMs">The TXDELAY restored when the verify fails, in ms (optional; default =
/// the port's configured <c>kiss.txDelay</c>, else 500).</param>
public sealed record TxDelayApplyRequest(
    string? PeerSdmId, int? TxDelayMs, int? Probes, bool? Persist, int? FallbackMs);

/// <summary>The RF caveat every txdelay-min mutating response carries (the keyup-pairing
/// pattern): these actions transmit.</summary>
public static class TxDelayMinCaveat
{
    /// <summary>The caveat text.</summary>
    public const string Text =
        "RF caveat: a coordinator/apply session KEYS THE TRANSMITTER repeatedly (separately-keyed " +
        "probe frames per sweep step) and pauses the port's normal AX.25 traffic until the session " +
        "ends and the port is restored. The meter role is passive but still pauses its port.";
}

/// <summary>
/// The guided deviation-tuning surface of the pdn node API — an operator-initiated, transmitting,
/// two-ended procedure coordinated over the radios' SDM side channel. Because a session KEYS THE
/// RADIO and pauses the port's normal AX.25 traffic, the mutating verbs are <b>admin</b>-scoped and
/// <b>audited</b> (mirroring the doctor's interrupt POST and the port-lifecycle endpoints); the live
/// event feed is <b>read</b>-scoped, pure observation.
/// <list type="bullet">
///   <item><c>POST   /api/v1/ports/{id}/tuning/session</c> — arm a session (404 unknown/not-running
///     port · 400 not a NinoTNC / no Tait radio / bad role or peer id / SDM disabled · 409 a session
///     is already active).</item>
///   <item><c>GET    /api/v1/ports/{id}/tuning/events</c> — SSE feed of rounds + lifecycle.</item>
///   <item><c>POST   /api/v1/ports/{id}/tuning/next</c> — the tuned operator's "I've adjusted the pot"
///     signal (409 when no round is awaiting / meter role).</item>
///   <item><c>POST   /api/v1/ports/{id}/tuning/stop</c> and <c>DELETE .../tuning/session</c> — stop
///     the session and restore the port.</item>
/// </list>
/// The port is <b>always restored</b> on session end, error, stop, or node shutdown.
/// </summary>
public static class PdnPortTuningApi
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>Map the tuning endpoints under <c>/api/v1</c>. Mapped before the SPA fallback so the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnPortTuningApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Live event feed: read-scoped, pure observation.
        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        read.MapGet("/ports/{id}/tuning/events", TuningEventsAsync);

        // Mutating verbs: admin-scoped + audited (a session transmits and pauses the port).
        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);
        admin.MapPost("/ports/{id}/tuning/session", StartAsync);
        admin.MapPost("/ports/{id}/tuning/next", NextAsync);
        admin.MapPost("/ports/{id}/tuning/stop", StopAsync);
        admin.MapDelete("/ports/{id}/tuning/session", StopAsync);

        // TXDELAY minimisation (docs/research/txdelay-optimisation.md, layer 2): the sweep +
        // the explicit apply. Same one-session-per-port claim, events feed and stop verbs as the
        // deviation sessions; same admin + audit + RF-caveat bar as keyup pairing (it transmits).
        admin.MapPost("/ports/{id}/tuning/txdelay-min", StartTxDelayMinAsync);
        admin.MapPost("/ports/{id}/tuning/txdelay-min/apply", StartTxDelayApplyAsync);
    }

    private static async Task<IResult> StartAsync(
        string id,
        TuningStartRequest? body,
        HttpContext ctx,
        PortTuningService tuning,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (body is null || !TuningPreflight.TryParseRole(body.Role, out var role))
        {
            return Results.BadRequest(new { error = "role must be 'tuned' or 'meter'" });
        }

        audit.RecordRest(
            ctx, clock, "port_tuning", id, "requested",
            $"role={body.Role} peer={body.PeerSdmId} burst={body.BurstFrames}");

        try
        {
            var info = await tuning.StartAsync(id, role, body.PeerSdmId ?? string.Empty, body.BurstFrames ?? 5, ct)
                .ConfigureAwait(false);
            return Results.Ok(info);
        }
        catch (TuningStartException ex)
        {
            return MapStartError(ex);
        }
    }

    private static IResult NextAsync(
        string id, HttpContext ctx, PortTuningService tuning, IAuditLog audit, TimeProvider clock)
    {
        audit.RecordRest(ctx, clock, "port_tuning_next", id, "requested");
        try
        {
            tuning.SignalNext(id);
            return Results.Ok(new { advanced = true });
        }
        catch (TuningStartException ex)
        {
            return MapStartError(ex);
        }
    }

    private static async Task<IResult> StopAsync(
        string id, HttpContext ctx, PortTuningService tuning, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        audit.RecordRest(ctx, clock, "port_tuning_stop", id, "requested");
        bool stopped = await tuning.StopAsync(id, ct).ConfigureAwait(false);
        return stopped
            ? Results.Ok(new { stopped = true })
            : Results.NotFound(new { error = $"no tuning session on port '{id}'" });
    }

    // ── TXDELAY minimisation (docs/research/txdelay-optimisation.md, layer 2) ──

    private static async Task<IResult> StartTxDelayMinAsync(
        string id,
        TxDelayMinStartRequest? body,
        HttpContext ctx,
        PortTuningService tuning,
        IConfigProvider config,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (body is null || body.Role is not ("coordinator" or "meter"))
        {
            return Results.BadRequest(new { error = "role must be 'coordinator' or 'meter'" });
        }
        bool coordinator = body.Role == "coordinator";

        audit.RecordRest(
            ctx, clock, "port_tuning_txdelay_min", id, "requested",
            $"role={body.Role} peer={body.PeerSdmId} start={body.StartMs} step={body.StepMs} " +
            $"min={body.MinMs} probes={body.ProbesPerStep} " +
            (coordinator ? "RF: keys separately-keyed probe frames per sweep step" : "passive meter"));

        var options = new TxDelayMinOptions
        {
            StartTxDelayMs = body.StartMs ?? ConfiguredTxDelayMs(config, id),
            StepMs = body.StepMs ?? 40,
            MinTxDelayMs = body.MinMs ?? 20,
            ProbesPerStep = Math.Clamp(body.ProbesPerStep ?? 5, 1, TxDelayMinResponder.MaxProbesPerStep),
        };
        if (coordinator && (options.StepMs <= 0 || options.StartTxDelayMs < options.MinTxDelayMs || options.MinTxDelayMs < 0))
        {
            return Results.BadRequest(new { error = "sweep needs stepMs > 0 and 0 <= minMs <= startMs" });
        }

        try
        {
            var info = await tuning
                .StartTxDelayMinAsync(id, coordinator, body.PeerSdmId ?? string.Empty, options, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { session = info, rfCaveat = TxDelayMinCaveat.Text });
        }
        catch (TuningStartException ex)
        {
            return MapStartError(ex);
        }
    }

    private static async Task<IResult> StartTxDelayApplyAsync(
        string id,
        TxDelayApplyRequest? body,
        HttpContext ctx,
        PortTuningService tuning,
        IWritableConfigProvider config,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (body?.TxDelayMs is not (> 0 and <= 2550))
        {
            return Results.BadRequest(new { error = "txDelayMs must be 1..2550 (the KISS byte range)" });
        }
        bool persist = body.Persist ?? true;

        audit.RecordRest(
            ctx, clock, "port_tuning_txdelay_apply", id, "requested",
            $"peer={body.PeerSdmId} txDelayMs={body.TxDelayMs} probes={body.Probes} persist={persist} " +
            "RF: keys verify probe frames");

        var options = new TxDelayMinOptions
        {
            // The value restored when the verify fails.
            StartTxDelayMs = body.FallbackMs ?? ConfiguredTxDelayMs(config, id),
            ProbesPerStep = Math.Clamp(body.Probes ?? 5, 1, TxDelayMinResponder.MaxProbesPerStep),
        };

        try
        {
            var info = await tuning.StartTxDelayApplyAsync(
                    id, body.PeerSdmId ?? string.Empty, body.TxDelayMs.Value, options,
                    persist ? (ms, c) => PersistTxDelayAsync(config, id, ms, c) : null, ct)
                .ConfigureAwait(false);
            return Results.Ok(new { session = info, rfCaveat = TxDelayMinCaveat.Text });
        }
        catch (TuningStartException ex)
        {
            return MapStartError(ex);
        }
    }

    /// <summary>The port's configured KISS TXDELAY in ms (the sweep's default start /
    /// the apply's default fallback), or the conventional 500 when unset.</summary>
    private static int ConfiguredTxDelayMs(IConfigProvider config, string portId)
    {
        var port = config.Current.Ports.FirstOrDefault(p => p.Id == portId);
        return port?.Kiss?.TxDelay is { } tenMs and > 0 ? tenMs * 10 : 500;
    }

    /// <summary>Persist a verified TXDELAY into the port's <c>kiss.txDelay</c> (10 ms
    /// units) via the same validate→apply path as the port editor. Runs on the apply
    /// session's background loop, so failures are reported, never thrown.</summary>
    private static Task<bool> PersistTxDelayAsync(
        IWritableConfigProvider config, string portId, int txDelayMs, CancellationToken ct)
    {
        _ = ct;
        var current = config.Current;
        var port = current.Ports.FirstOrDefault(p => p.Id == portId);
        if (port is null)
        {
            return Task.FromResult(false);
        }
        byte tenMs = (byte)Math.Clamp((txDelayMs + 9) / 10, 1, 255);
        var updated = port with { Kiss = (port.Kiss ?? new KissParams()) with { TxDelay = tenMs } };
        var candidate = current with
        {
            Ports = current.Ports.Select(p => ReferenceEquals(p, port) ? updated : p).ToList(),
        };
        return Task.FromResult(config.TryApply(candidate, out _));
    }

    private static IResult MapStartError(TuningStartException ex) => ex.Error switch
    {
        TuningStartError.NotFound => Results.NotFound(new { error = ex.Message }),
        TuningStartError.Conflict => Results.Conflict(new { error = ex.Message }),
        _ => Results.BadRequest(new { error = ex.Message }),
    };

    private static async Task TuningEventsAsync(string id, HttpContext ctx, PortTuningService tuning, TimeProvider clock)
    {
        var session = tuning.Get(id);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = ctx.RequestAborted;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var sub = session.Subscribe(out var reader);

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
                    // The session ended: its feed completed. Nothing more will arrive.
                    break;
                }

                while (reader.TryRead(out var evt))
                {
                    var json = JsonSerializer.Serialize(evt, JsonSerializerOptions.Web);
                    await WriteAsync(ctx, $"event: tuning\ndata: {json}\n\n", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away (RequestAborted) — normal SSE teardown.
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
            // Client disconnected mid-write — expected.
        }
        catch (IOException)
        {
            // Broken pipe to a vanished client — expected.
        }
    }
}
