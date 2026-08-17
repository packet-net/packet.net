using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The browser-facing node command console API: a privileged surface that opens the node's own
/// sysop command console (the telnet-equivalent shell where you type <c>ports</c>, <c>nodes</c>,
/// <c>connect</c>, etc.) over an in-process bridge and streams it to an xterm.js terminal in the
/// SPA. This is NOT the per-AX.25-session console the Sessions screen exposes — it is the node's
/// own command processor (<see cref="NodeCommandService"/>), the same one the telnet listener runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bridge.</b> <c>POST /api/v1/console</c> asks <see cref="NodeHostedService.OpenConsoleSession"/>
/// to build a <see cref="LoopbackNodeConnection"/> pair, run a freshly-wired
/// <see cref="NodeCommandService"/> over the app-end (the same wiring the telnet console uses), and
/// adopt the user-end into the <see cref="SysopConsoleManager"/>. Reusing the manager means this
/// API needs no SSE/input plumbing of its own: <c>GET .../stream</c> and <c>POST .../input</c> drive
/// the exact same fan-out + write path the Sessions console drawer already uses. The minted id is
/// <c>console:&lt;guid&gt;</c> — distinct from the <c>{portId}:{peer}</c> ids the AX.25 connect-out
/// sessions carry, so the two share the manager without colliding.
/// </para>
/// <para>
/// <b>Line discipline.</b> The console's <c>LineAssembler</c> splits inbound bytes on CR / LF / CR-LF,
/// so input is line-oriented, not raw keystrokes. The terminal sends a bare CR when the user presses
/// Enter (xterm's convention), which we forward verbatim — the assembler treats it as a complete line
/// and the command runs. Intermediate keystrokes are forwarded as typed; the loopback transport is
/// <see cref="NodeTransportKind.Telnet"/>, so the console echoes/line-edits locally and emits CR-LF.
/// </para>
/// <para>
/// <b>Auth + audit.</b> The whole group is admin-gated (<see cref="PdnAuthPolicies.Admin"/>) — the
/// node command console is the node's most privileged surface (it reaches the sysop verbs and
/// connect-out). The open is audited (an operator opened a privileged shell), matching the
/// MCP-token / session-connect audit pattern; the per-keystroke input is NOT audited (it would
/// drown the log and may carry secrets — e.g. a SYSOP code typed at the prompt).
/// </para>
/// <para>
/// <b>Lifecycle.</b> A closed/abandoned console must not leak a running <see cref="NodeCommandService"/>.
/// <c>DELETE /api/v1/console/{id}</c> closes the manager session (disposing the user-end → the app-end
/// reads EOF → the service loop exits → the app-end is disposed). A peer-gone (the service itself
/// exiting, e.g. the user typed <c>Bye</c>) tears the manager session down via the manager's
/// peer-gone callback. The manager is disposed on host shutdown, closing any survivors.
/// </para>
/// </remarks>
public static class PdnConsoleApi
{
    /// <summary>
    /// Map the node-command-console endpoints under <c>/api/v1</c>, admin-gated. Called from the
    /// node composition root before the SPA fallback (the specific routes win over the
    /// <c>/api/{**rest}</c> catch-all regardless of order).
    /// </summary>
    public static void MapPdnConsoleApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The node command console is the node's most privileged surface — admin only. The gate is
        // a no-op when management.auth.enabled is off (ScopeRequirementHandler passes through).
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);

        // Open a new node command console session: build the loopback-bridged NodeCommandService
        // and adopt it into the SysopConsoleManager. Returns the minted id.
        v1.MapPost("/console", (
            HttpContext ctx, NodeHostedService host, SysopConsoleManager console,
            IAuditLog audit, TimeProvider clock) =>
        {
            var id = host.OpenConsoleSession(console);

            // Audit the open: an operator opened the node's privileged command shell.
            audit.RecordRest(ctx, clock, "open_console", id, "ok", "");

            return Results.Ok(new ConsoleOpenResponse(id));
        });

        // Interactive output stream (Server-Sent Events) — mirrors /sessions/{id}/stream exactly:
        // subscribe (and thus 404-check) BEFORE writing any bytes; replay the backlog as one
        // `backlog` event; then stream live chunks as `output` (each JSON-encoded as a string so
        // embedded CR/LF survive SSE's line framing); a `: ping` heartbeat keeps it warm; no-store.
        v1.MapGet("/console/{id}/stream", async (string id, HttpContext ctx, SysopConsoleManager console, TimeProvider clock) =>
        {
            var ct = ctx.RequestAborted;

            using var sub = console.Subscribe(id, out var backlog, out var reader);
            if (sub is null || reader is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            SseWriter.Begin(ctx);

            // Replay the backlog first (the banner/prompt the browser missed) as one `backlog`
            // event, NOT `output`: the replay is sent on every subscription, so a client that
            // appended it as live output duplicated the whole history on each silent EventSource
            // reconnect (C045). The distinct name lets the client reset its terminal first.
            // Even an empty backlog is sent so the client's onopen-driven render has a
            // deterministic first event and the headers flush.
            await WriteBacklogAsync(ctx, backlog, ct).ConfigureAwait(false);

            // Each chunk is JSON-encoded as a string (see WriteChunkAsync) so embedded CR/LF
            // survive SSE's line framing. The pump ends when the console exits (Bye), the
            // session is closed, or the client goes away.
            await SseWriter.PumpAsync(ctx, reader, clock, "output", chunk => JsonSerializer.Serialize(chunk), ct)
                .ConfigureAwait(false);
        })
          // A browser EventSource can't set an Authorization header - this marker is what
          // lets the JWT ride as ?access_token= on THIS route (see AcceptsQueryAccessToken).
          .WithMetadata(AcceptsQueryAccessToken.Instance);

        // Feed input to the console. The body's `data` is forwarded verbatim (UTF-8) into the
        // loopback's user-end via the manager's write path; the console's LineAssembler does the
        // line splitting (CR/LF/CR-LF). xterm sends a bare CR on Enter, which completes a line.
        // 404 when the id isn't managed (closed / never opened), else 202 (queued).
        v1.MapPost("/console/{id}/input", async (string id, ConsoleInputRequest body, SysopConsoleManager console, CancellationToken ct) =>
        {
            if (body is null || body.Data is null)
            {
                return Results.BadRequest(new { error = "A 'data' field is required." });
            }
            if (!console.IsManaged(id))
            {
                return Results.NotFound();
            }

            await console.WriteAsync(id, Encoding.UTF8.GetBytes(body.Data), ct).ConfigureAwait(false);
            return Results.Accepted();
        });

        // Close + dispose the console session: stop the read pump, dispose the user-end (→ the
        // app-end reads EOF → the NodeCommandService loop exits → the app-end is disposed), so no
        // running console is leaked. 204 whether or not the id was managed (idempotent teardown).
        v1.MapDelete("/console/{id}", async (string id, HttpContext ctx, SysopConsoleManager console, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            audit.RecordRest(ctx, clock, "close_console", id, "requested", "");
            await console.CloseAsync(id).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    /// <summary>The open-console response: the minted session id the stream/input/close
    /// endpoints address.</summary>
    public sealed record ConsoleOpenResponse(string Id);

    /// <summary>The input request body: the raw text to feed into the console (forwarded
    /// verbatim; the console's line discipline splits it).</summary>
    public sealed record ConsoleInputRequest(string Data);

    // Emit one `output` SSE event carrying a text chunk, JSON-encoded as a string so embedded
    // CR/LF survive SSE's line framing (a raw \n in a data: line would terminate the event early).
    // Identical to PdnSessionsApi's encoding so the frontend's one decoder serves both streams.
    private static Task WriteOutputAsync(HttpContext ctx, string chunk, CancellationToken ct)
        => WriteChunkAsync(ctx, "output", chunk, ct);

    // The replayed history, as a DISTINCT `backlog` event. Every subscription (including the
    // browser's silent EventSource auto-reconnect) starts with the whole snapshot, so a client
    // that appended it as `output` rewrote its history on each reconnect (review item C045,
    // #689). Naming the replay lets the client reset its terminal before writing it.
    private static Task WriteBacklogAsync(HttpContext ctx, string chunk, CancellationToken ct)
        => WriteChunkAsync(ctx, "backlog", chunk, ct);

    private static Task WriteChunkAsync(HttpContext ctx, string eventName, string chunk, CancellationToken ct)
        => SseWriter.EventAsync(ctx, eventName, JsonSerializer.Serialize(chunk), ct);
}
