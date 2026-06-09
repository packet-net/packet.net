using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The write side of the pdn node control API (Slice 3, step 2). Maps the
/// <c>PUT /api/v1/config</c> family the web editor uses to persist an edit:
/// validate a candidate <see cref="NodeConfig"/>, show the operator what applying
/// it would disrupt (the <see cref="ReconcilePreview"/>), and — unless it was a
/// dry-run — persist it through the <see cref="IWritableConfigProvider"/> write
/// seam (which advances <c>Current</c> + raises <c>OnChange</c>, driving the same
/// hot reconcile a hand-edit of the file would).
/// </summary>
/// <remarks>
/// <para>
/// Two body shapes, the same flow. <c>PUT /config</c> takes the structured
/// <see cref="NodeConfig"/> JSON (the polymorphic <c>transport</c> union is bound
/// by the <see cref="TransportConfigJsonConverter"/> registered in
/// <c>Program.cs</c>); <c>PUT /config/raw</c> takes the raw YAML text the advanced
/// editor round-trips through <c>GET /config/raw</c>. Both validate first and
/// return 422 (<see cref="ValidationProblem"/>) on a rejected candidate without
/// touching the running node; a malformed raw-YAML body is itself a 422 (the parse
/// failure surfaced as a single <c>(yaml)</c>-path error). The preview is always
/// computed from the <b>live</b> config to the candidate, captured before any
/// apply.
/// </para>
/// <para>
/// Auth is a later step — like the read API and SSE feed, these are unauthenticated
/// and the node binds 127.0.0.1 by default. No wall-clock here (repo rule §2.7):
/// the config path needs no clock at all.
/// </para>
/// </remarks>
public static class PdnConfigApi
{
    /// <summary>
    /// Map the write-side config endpoints under <c>/api/v1</c>. Called from the
    /// node composition root after the read API + SSE feed and before the SPA
    /// fallback (the specific routes win over the <c>/api/{**rest}</c> catch-all
    /// regardless of order).
    /// </summary>
    public static void MapPdnConfigApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1");

        // Structured edit: a NodeConfig JSON body. ?dryRun=true validates +
        // previews without persisting.
        v1.MapPut("/config", (NodeConfig candidate, IWritableConfigProvider cfg, bool dryRun = false) =>
        {
            // Capture the live config BEFORE applying — the preview is from→to.
            var before = cfg.Current;

            var errors = cfg.Validate(candidate);
            if (errors.Count > 0)
            {
                return Results.UnprocessableEntity(new ValidationProblem(errors));
            }

            var preview = ReconcilePreviewBuilder.Build(before, candidate);
            if (dryRun)
            {
                return Results.Ok(ToResult(preview, applied: false));
            }

            // Defensive: TryApply re-validates, so after a clean Validate it should
            // always succeed — but honour its verdict rather than assume.
            if (!cfg.TryApply(candidate, out var applyErrors))
            {
                return Results.UnprocessableEntity(new ValidationProblem(applyErrors));
            }
            return Results.Ok(ToResult(preview, applied: true));
        });

        // The advanced editor reads the live config as raw YAML to edit by hand.
        v1.MapGet("/config/raw", (IWritableConfigProvider cfg) =>
            Results.Text(NodeConfigYaml.Serialize(cfg.Current), "text/plain"));

        // Raw-YAML edit: the request body IS the YAML. A parse failure is a 422 with
        // a single (yaml)-path error; otherwise the same validate→preview→apply flow.
        v1.MapPut("/config/raw", async (HttpContext ctx, IWritableConfigProvider cfg, bool dryRun = false) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var yaml = await reader.ReadToEndAsync();

            NodeConfig candidate;
            try
            {
                candidate = NodeConfigYaml.Parse(yaml);
            }
            catch (Exception ex)
            {
                return Results.UnprocessableEntity(
                    new ValidationProblem([new ConfigValidationError("(yaml)", ex.Message)]));
            }

            var before = cfg.Current;

            var errors = cfg.Validate(candidate);
            if (errors.Count > 0)
            {
                return Results.UnprocessableEntity(new ValidationProblem(errors));
            }

            var preview = ReconcilePreviewBuilder.Build(before, candidate);
            if (dryRun)
            {
                return Results.Ok(ToResult(preview, applied: false));
            }

            if (!cfg.TryApply(candidate, out var applyErrors))
            {
                return Results.UnprocessableEntity(new ValidationProblem(applyErrors));
            }
            return Results.Ok(ToResult(preview, applied: true));
        });
    }

    /// <summary>Project a <see cref="ReconcilePreview"/> to the PUT result, carrying
    /// the four change buckets through and tagging whether it was actually applied.</summary>
    private static ReconcileResult ToResult(ReconcilePreview preview, bool applied) =>
        new(preview.Valid, preview.Live, preview.PortRestart, preview.NodeReset, applied);
}
