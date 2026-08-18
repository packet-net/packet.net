using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Api;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The write side of the pdn node control API (Slice 3, step 2). Maps the
/// <c>PUT /api/v1/config</c> family the web editor uses to persist an edit:
/// validate a candidate <see cref="NodeConfig"/>, show the operator what applying
/// it would disrupt (the <see cref="ReconcilePreview"/>), and - unless it was a
/// dry-run - persist it through the <see cref="IWritableConfigProvider"/> write
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
/// <b>Optimistic concurrency.</b> Both PUTs accept an <c>If-Match</c> carrying the version
/// token <c>GET /config</c> (and <c>GET /config/raw</c>) serve as an <c>ETag</c>. It is
/// compared inside the provider's write lock, so an edit built on a document another writer
/// has since replaced is refused with <c>412 Precondition Failed</c> instead of silently
/// overwriting it (review item C065, #694). No header = last-writer-wins, exactly as before.
/// A successful apply answers with the new version's <c>ETag</c>. See <see cref="ConfigCas"/>.
/// </para>
/// <para>
/// <b>Scope.</b> A config write is <c>operate</c> (the shipped model - plan.md §5.4) and
/// reading the raw YAML is <c>read</c>, with one exception: a write that changes the
/// <c>management.auth</c> block needs <c>admin</c>, because that block IS the gate and an
/// operate user who can disable it is an admin in all but name (review item C020).
/// </para>
/// <para>
/// <b>Secrets.</b> The read projections mask <c>tailscale.authKey</c>, <c>mqtt.password</c>
/// and <c>management.https.certificatePassword</c>; a write that echoes the mask back keeps
/// the stored value. See <see cref="ConfigRedaction"/>.
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
        v1.MapPut("/config", (NodeConfig candidate, HttpContext ctx, IWritableConfigProvider cfg, NodeHostedService host, IAuditLog audit, TimeProvider clock, bool dryRun = false) =>
        {
            // Capture the live config BEFORE applying - the preview is from→to.
            var before = cfg.Current;

            // A round-tripped edit carries "***" wherever the read projection masked a secret;
            // restore the stored value so an edit elsewhere in the file can't wipe the tailnet
            // key / broker password / PKCS#12 password (C010). This stays AHEAD of Validate on
            // purpose - the validator must see the real stored secrets, not placeholders - and
            // it is null-safe about a candidate whose blocks are explicit nulls, so a malformed
            // body reaches the validator and comes back a 422 rather than a 500 (#727 item 4).
            candidate = ConfigRedaction.Unredact(candidate, before);

            if (AuthBlockChanged(before, candidate) && !IsAdmin(ctx, before))
            {
                return AuthChangeForbidden();
            }

            var errors = cfg.Validate(candidate);
            if (errors.Count > 0)
            {
                return Results.UnprocessableEntity(new ValidationProblem(errors));
            }

            if (LiveConflicts(host, candidate) is { Count: > 0 } live)
            {
                return Results.UnprocessableEntity(new ValidationProblem(live));
            }

            var preview = ReconcilePreviewBuilder.Build(before, candidate);
            if (dryRun)
            {
                return Results.Ok(ToResult(preview, applied: false));
            }

            // Apply under the caller's If-Match (null = last-writer-wins). Defensive: Apply
            // re-validates, so after a clean Validate only a CAS refusal or a store fault
            // should stop it, but honour its verdict rather than assume.
            var applied = cfg.Apply(candidate, ConfigCas.ExpectedVersion(ctx, cfg));
            if (ConfigCas.Refusal(ctx, applied) is { } refused)
            {
                return refused;
            }
            // Audit the applied config write (not dry-runs, not rejected candidates).
            audit.RecordRest(ctx, clock, "PUT /config", "config", "ok",
                $"portRestart={preview.PortRestart} nodeReset={preview.NodeReset}");
            return Results.Ok(ToResult(preview, applied: true));
        }).RequireAuthorization(PdnAuthPolicies.Operate);   // a config write is `operate`

        // The advanced editor reads the live config as raw YAML to edit by hand. The ETag is
        // the document version to send back as If-Match on the PUT (C065); secrets are masked
        // exactly as on GET /config (C010).
        v1.MapGet("/config/raw", (HttpContext ctx, IWritableConfigProvider cfg) =>
        {
            ConfigCas.SetETag(ctx, cfg);
            return Results.Text(NodeConfigYaml.Serialize(ConfigRedaction.Redact(cfg.Current)), "text/plain");
        }).RequireAuthorization(PdnAuthPolicies.Read);    // reading config is `read`

        // Raw-YAML edit: the request body IS the YAML. A parse failure is a 422 with
        // a single (yaml)-path error; otherwise the same validate→preview→apply flow.
        v1.MapPut("/config/raw", async (HttpContext ctx, IWritableConfigProvider cfg, NodeHostedService host, IAuditLog audit, TimeProvider clock, bool dryRun = false) =>
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

            // Same placeholder contract as the structured PUT: the advanced editor reads
            // masked YAML, so "***" means keep-current (C010). Null-safe about an emptied
            // `mqtt:` / `tailscale:` key, which YamlDotNet parses as a null block (#727 item 4).
            candidate = ConfigRedaction.Unredact(candidate, before);

            if (AuthBlockChanged(before, candidate) && !IsAdmin(ctx, before))
            {
                return AuthChangeForbidden();
            }

            var errors = cfg.Validate(candidate);
            if (errors.Count > 0)
            {
                return Results.UnprocessableEntity(new ValidationProblem(errors));
            }

            if (LiveConflicts(host, candidate) is { Count: > 0 } live)
            {
                return Results.UnprocessableEntity(new ValidationProblem(live));
            }

            var preview = ReconcilePreviewBuilder.Build(before, candidate);
            if (dryRun)
            {
                return Results.Ok(ToResult(preview, applied: false));
            }

            var applied = cfg.Apply(candidate, ConfigCas.ExpectedVersion(ctx, cfg));
            if (ConfigCas.Refusal(ctx, applied) is { } refused)
            {
                return refused;
            }
            audit.RecordRest(ctx, clock, "PUT /config/raw", "config", "ok",
                $"portRestart={preview.PortRestart} nodeReset={preview.NodeReset}");
            return Results.Ok(ToResult(preview, applied: true));
        }).RequireAuthorization(PdnAuthPolicies.Operate);   // a raw-YAML config write is `operate`
    }

    // --- the management.auth guard (C020) ---------------------------------------
    //
    // A config write is `operate` (the shipped model, plan.md §5.4) - but the auth block is
    // the gate itself: an operate user could PUT management.auth.enabled=false and turn the
    // whole node open, which makes operate silently equal to admin. So a write that CHANGES
    // the auth block needs `admin`, while every other config write stays `operate`.
    //
    // Compared by RECORD VALUE EQUALITY, which is what AuthConfig already offers. The
    // hand-rolled field compare this replaces was justified by a comment claiming record
    // equality on the origins list would be reference equality; that was wrong -
    // WebAuthnConfig ships an explicit Equals over the null-safe ConfigEquality.ListEqual for
    // exactly this reason, and AuthConfig's compiler-generated Equals uses it. The hand-rolled
    // version instead dereferenced `WebAuthn.AllowedOrigins` on both sides with no guard, so a
    // candidate carrying `allowedOrigins: null` was an ArgumentNullException (a 500 rather than
    // the validator's 422), and once such a config had been PERSISTED the live side was null
    // too and EVERY later config write on that node 500'd permanently, with no error text to
    // explain it (#727 item 4).
    //
    // The null-conditional walk down to Auth matters for the same reason: STJ and YamlDotNet
    // both put a real null on the non-nullable Management property for an explicit null block.
    // Two nulls compare equal (no auth change), and a null on one side only is a change, which
    // is the conservative answer: it demands admin and then the validator rejects it.
    private static bool AuthBlockChanged(NodeConfig before, NodeConfig after) =>
        before.Management?.Auth != after.Management?.Auth;

    // Admin for the purposes of that guard. With auth OFF there is no principal and no gate to
    // protect (the whole API is open), so the guard passes through exactly like every other
    // scope check does - it must not block the first-run "turn auth on" write.
    private static bool IsAdmin(HttpContext ctx, NodeConfig before) =>
        // A null management/auth block in the LIVE config (see AuthBlockChanged) reads as
        // "auth is on": unknown must not open the gate.
        (before.Management?.Auth?.Enabled ?? true) is false
        || AuthScopes.Satisfies(ctx.User.FindFirst(AuthScopes.ScopeClaim)?.Value, AuthScopes.Admin);

    private static IResult AuthChangeForbidden() =>
        Results.Problem(
            "Changing management.auth requires the admin scope.",
            statusCode: StatusCodes.Status403Forbidden);

    // --- the LIVE-state gate ----------------------------------------------------
    //
    // The validator is pure: it reads a candidate config and nothing else. Some conflicts only
    // exist against RUNNING state - today, an identity.callsign that an application has already
    // bound over RHP (#723 item 2). The supervisor refuses such an apply outright, and it will
    // still refuse a config written round the back (a hand-edited conffile), but an operator
    // driving the panel deserves the answer BEFORE the write is persisted rather than an Error in
    // the journal afterwards. So the same check runs here, ahead of persistence, and comes back
    // as the 422 ValidationProblem the editor already knows how to render against a field.
    private static IReadOnlyList<ConfigValidationError> LiveConflicts(NodeHostedService host, NodeConfig candidate)
        => host.Supervisor is null
            ? []
            : [.. host.Supervisor.LiveApplyConflicts(candidate)
                .Select(reason => new ConfigValidationError("identity.callsign", reason))];

    /// <summary>Project a <see cref="ReconcilePreview"/> to the PUT result, carrying
    /// the four change buckets through and tagging whether it was actually applied.</summary>
    private static ReconcileResult ToResult(ReconcilePreview preview, bool applied) =>
        new(preview.Valid, preview.Live, preview.PortRestart, preview.NodeReset, applied);
}
