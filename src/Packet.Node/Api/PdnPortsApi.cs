using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The port-management side of the pdn node control API (Slice 3, step 3). Maps the
/// <c>/api/v1/ports</c> CRUD + lifecycle family the web Ports screen uses to add,
/// edit, remove, and enable/disable a port — every mutation expressed as a candidate
/// <see cref="NodeConfig"/> persisted through the <b>same</b>
/// <see cref="IWritableConfigProvider"/> write seam the config editor uses (validate →
/// <see cref="ReconcilePreview"/> → <c>TryApply</c>, which advances <c>Current</c> +
/// raises <c>OnChange</c> and so drives the same hot reconcile a hand-edit of the file
/// would). There is no new supervisor surface here: a port coming up or down is just an
/// <see cref="PortConfig.Enabled"/> flip flowing through the config reconcile.
/// </summary>
/// <remarks>
/// <para>
/// The four mutating routes (<c>POST /ports</c>, <c>PUT /ports/{id}</c>,
/// <c>DELETE /ports/{id}</c>, and the <c>up</c>/<c>down</c> arms of
/// <c>POST /ports/{id}/lifecycle</c>) all reduce to "build the new <c>Ports</c> list,
/// then run the shared validate→preview→apply" — the same three steps as
/// <see cref="PdnConfigApi"/>, factored into <see cref="ApplyCandidate"/>. Add/edit/delete
/// return a <see cref="ReconcileResult"/> (or 422 <see cref="ValidationProblem"/> on a
/// rejected candidate, 404 when the id is unknown); lifecycle up/down apply and then
/// project the port's resulting <see cref="PortStatus"/>.
/// </para>
/// <para>
/// A duplicate-id add is rejected by the validator (its <c>HaveUniqueIds</c> rule), so it
/// surfaces naturally as a 422 — no separate guard needed. The polymorphic
/// <c>transport</c> union in a <see cref="PortConfig"/> body binds via the
/// <c>TransportConfigJsonConverter</c> registered globally in <c>Program.cs</c>, exactly
/// as the <c>PUT /config</c> body does.
/// </para>
/// <para>
/// The lifecycle <c>restart</c> action is <b>deferred</b> — it needs a serialized
/// supervisor entry point that is out of scope for this config-write step — and returns
/// 501 with an explicit message rather than silently no-op'ing or faking a config flip.
/// Reconcile is asynchronous (the <c>OnChange</c> handler hands off to a serialized
/// worker), so the <see cref="PortStatus"/> returned right after an up/down apply is
/// best-effort: the port may still read <c>down</c>/<c>faulted</c> for an instant before
/// the worker brings it up. That is honest, not a bug.
/// </para>
/// <para>
/// Auth is a later step — like the read API, the SSE feed, and the config write API,
/// these are unauthenticated and the node binds 127.0.0.1 by default. No wall-clock here
/// (repo rule §2.7): the config path needs no clock at all.
/// </para>
/// </remarks>
public static class PdnPortsApi
{
    /// <summary>
    /// Map the port-management endpoints under <c>/api/v1</c>. Called from the node
    /// composition root after the config write API and before the SPA fallback (the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all regardless of order).
    /// </summary>
    public static void MapPdnPortsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1");

        // Add a port: append it to the live Ports list. A duplicate id is caught by the
        // validator's unique-id rule → 422 (no separate guard needed here).
        v1.MapPost("/ports", (PortConfig port, IWritableConfigProvider cfg) =>
        {
            var candidate = cfg.Current with { Ports = [.. cfg.Current.Ports, port] };
            return ApplyCandidate(cfg, candidate);
        });

        // Edit a port: replace the port carrying {id}. Unknown id → 404. Renaming the id
        // in the body (Id != {id}) reads as "edit the {id} entry" — the route id is the key.
        v1.MapPut("/ports/{id}", (string id, PortConfig port, IWritableConfigProvider cfg) =>
        {
            if (!cfg.Current.Ports.Any(p => p.Id == id))
            {
                return Results.NotFound();
            }
            var ports = cfg.Current.Ports.Select(p => p.Id == id ? port : p).ToArray();
            var candidate = cfg.Current with { Ports = ports };
            return ApplyCandidate(cfg, candidate);
        });

        // Remove a port. Unknown id → 404.
        v1.MapDelete("/ports/{id}", (string id, IWritableConfigProvider cfg) =>
        {
            if (!cfg.Current.Ports.Any(p => p.Id == id))
            {
                return Results.NotFound();
            }
            var candidate = cfg.Current with { Ports = [.. cfg.Current.Ports.Where(p => p.Id != id)] };
            return ApplyCandidate(cfg, candidate);
        });

        // up/down: flip Enabled and apply through the config seam, then return the port's
        // resulting (best-effort) PortStatus. restart is deferred — see the type remarks.
        v1.MapPost("/ports/{id}/lifecycle",
            (string id, LifecycleRequest body, IWritableConfigProvider cfg, NodeHostedService host) =>
        {
            var existing = cfg.Current.Ports.FirstOrDefault(p => p.Id == id);
            if (existing is null)
            {
                return Results.NotFound();
            }

            switch (body.Action)
            {
                case "up":
                case "down":
                    bool enabled = body.Action == "up";
                    var ports = cfg.Current.Ports
                        .Select(p => p.Id == id ? p with { Enabled = enabled } : p)
                        .ToArray();
                    // Re-validate + apply through the seam. A flip can in principle be
                    // rejected (e.g. enabling a port whose endpoint now collides) — honour
                    // that as a 422 rather than assume it always succeeds.
                    if (!cfg.TryApply(cfg.Current with { Ports = ports }, out var errors))
                    {
                        return Results.UnprocessableEntity(new ValidationProblem(errors));
                    }
                    // Best-effort projection: reconcile is async, so the port may not have
                    // finished coming up the instant we read it (see type remarks).
                    return Results.Ok(ProjectPort(host, cfg, id));

                case "restart":
                    // DEFERRED: a transient restart needs a serialized supervisor entry
                    // point (stop + restart one port without a config change) that this
                    // config-write step does not introduce. Be explicit, not silent.
                    return Results.Json(
                        new { error = "Port restart is not yet implemented — it is a later step (needs a serialized supervisor action). Toggle the port down then up to force a reconcile, or edit it to trigger a restart." },
                        statusCode: StatusCodes.Status501NotImplemented);

                default:
                    return Results.BadRequest(
                        new { error = $"Unknown lifecycle action '{body.Action}' (expected 'up', 'down', or 'restart')." });
            }
        });
    }

    /// <summary>The lifecycle request body: <c>{ "action": "up" | "down" | "restart" }</c>.</summary>
    public sealed record LifecycleRequest(string Action);

    /// <summary>
    /// The shared validate→preview→apply for a port mutation: capture the live config as
    /// <c>before</c>, validate the candidate (422 on failure without touching the node),
    /// build the from→to <see cref="ReconcilePreview"/>, persist via <c>TryApply</c>, and
    /// return the <see cref="ReconcileResult"/>. Same three steps as <see cref="PdnConfigApi"/>.
    /// </summary>
    private static IResult ApplyCandidate(IWritableConfigProvider cfg, NodeConfig candidate)
    {
        var before = cfg.Current;

        var errors = cfg.Validate(candidate);
        if (errors.Count > 0)
        {
            return Results.UnprocessableEntity(new ValidationProblem(errors));
        }

        var preview = ReconcilePreviewBuilder.Build(before, candidate);

        // Defensive: TryApply re-validates, so after a clean Validate it should always
        // succeed — but honour its verdict rather than assume.
        if (!cfg.TryApply(candidate, out var applyErrors))
        {
            return Results.UnprocessableEntity(new ValidationProblem(applyErrors));
        }

        return Results.Ok(new ReconcileResult(
            preview.Valid, preview.Live, preview.PortRestart, preview.NodeReset, Applied: true));
    }

    /// <summary>
    /// Project the live <see cref="PortStatus"/> for one configured port — the same
    /// per-port shape <c>PdnReadApi.BuildPorts</c> projects, narrowed to a single id. The
    /// live state comes from the supervisor's <see cref="RunningPort"/> (if it is running)
    /// plus the per-port telemetry counters; the enabled flag + existence come from the
    /// (just-applied) config. Returns a synthetic <c>down</c> status if the id has already
    /// vanished from config (shouldn't happen on an up/down flip, but stay total).
    /// </summary>
    private static PortStatus ProjectPort(NodeHostedService host, IWritableConfigProvider cfg, string id)
    {
        var port = cfg.Current.Ports.FirstOrDefault(p => p.Id == id);
        if (port is null)
        {
            return new PortStatus(id, Enabled: false, State: "down", SessionCount: 0,
                LastError: null, FramesIn: 0, FramesOut: 0);
        }

        var running = host.Supervisor?.GetPort(port.Id);
        string state;
        int sessions = 0;

        if (!port.Enabled)
        {
            // Configured but switched off — not running by design.
            state = "down";
        }
        else if (running is { Started: true })
        {
            state = "up";
            sessions = running.Listener.ActiveSessions.Count;
        }
        else
        {
            // Enabled but either not (yet) running — the async reconcile may not have
            // brought it up — or running in a faulted-bring-up state. Either way it's not
            // serving yet. Best-effort + honest (see the type remarks).
            state = "faulted";
        }

        var (framesIn, framesOut) = host.Telemetry.PortFrames(port.Id);

        return new PortStatus(
            Id: port.Id,
            Enabled: port.Enabled,
            State: state,
            SessionCount: sessions,
            LastError: null,        // per-port last-error not surfaced yet (mirrors PdnReadApi).
            FramesIn: framesIn,
            FramesOut: framesOut);
    }
}
