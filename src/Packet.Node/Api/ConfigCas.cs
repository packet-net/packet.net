using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The compare-and-swap plumbing shared by every config read-modify-write endpoint
/// (<c>PUT /config</c>, <c>PUT /config/raw</c>, the <c>/ports</c> family, the radio edits):
/// read <c>If-Match</c> off the request, stamp the document's version as an <c>ETag</c> on the
/// response, and turn a provider verdict into the right status code.
/// </summary>
/// <remarks>
/// <para>
/// Every one of those endpoints does a read-modify-write of the WHOLE config document
/// (<c>cfg.Current with { ... }</c>), so two writers that overlap used to end with the second
/// silently discarding the first's edit, both getting a 200 (review item C065, #694). A client
/// that echoes the <c>ETag</c> it read as <c>If-Match</c> now gets a <c>412 Precondition
/// Failed</c> instead, carrying the version actually in force so it can re-read and retry.
/// </para>
/// <para>
/// <b>The header is optional.</b> Omitting it keeps the historical last-writer-wins behaviour,
/// so existing clients (and curl) are unaffected; only a client that opts in gets the
/// guarantee. <c>If-Match: *</c> means "as long as there is a config", which there always is.
/// </para>
/// </remarks>
internal static class ConfigCas
{
    /// <summary>The <c>If-Match</c> request header, or null when absent/blank (= no CAS).</summary>
    public static string? IfMatch(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var raw = ctx.Request.Headers.IfMatch.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// Resolve an <c>If-Match</c> header to the version token to hand the provider: null when
    /// the header is absent (no CAS) or is the <c>*</c> wildcard (matches any live document).
    /// </summary>
    public static string? ExpectedVersion(HttpContext ctx, IWritableConfigProvider cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var header = IfMatch(ctx);
        if (header is null || header.Trim() == "*")
        {
            return null;
        }
        // Normalise the entity-tag syntax ("v", W/"v", or a list) down to the bare token the
        // provider compares. A header that matches the live version resolves to it; one that
        // doesn't resolves to the raw header, which the provider then refuses under its gate.
        var live = cfg.CurrentVersion;
        return ConfigVersion.Matches(header, live) ? live : header.Trim().Trim('"');
    }

    /// <summary>Stamp the config document's current version on the response as an
    /// <c>ETag</c>, so the caller can send it back as <c>If-Match</c> on its next write.</summary>
    public static void SetETag(HttpContext ctx, IWritableConfigProvider cfg)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(cfg);
        SetETag(ctx, cfg.CurrentVersion);
    }

    /// <summary>Stamp a known version on the response as an <c>ETag</c>.</summary>
    public static void SetETag(HttpContext ctx, string version)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Response.Headers.ETag = ConfigVersion.ToETag(version);
    }

    /// <summary>
    /// The failure response for an apply that did not happen, or null when it did. A version
    /// mismatch is a 412 carrying the live version (and an <c>ETag</c>, so a client can retry
    /// straight from the response); anything else is the usual 422 <see cref="ValidationProblem"/>.
    /// </summary>
    public static IResult? Refusal(HttpContext ctx, ConfigApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case ConfigApplyOutcome.Applied:
                SetETag(ctx, result.Version);
                return null;

            case ConfigApplyOutcome.VersionMismatch:
                SetETag(ctx, result.Version);
                return Results.Json(
                    new
                    {
                        error = "The config changed since you read it (another writer landed first). "
                            + "Re-read GET /api/v1/config and re-apply your edit.",
                        version = result.Version,
                    },
                    statusCode: StatusCodes.Status412PreconditionFailed);

            default:
                return Results.UnprocessableEntity(new ValidationProblem(result.Errors));
        }
    }
}
