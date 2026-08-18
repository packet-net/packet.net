using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The MCP management API: minting the long-lived **MCP bearer token** an operator
/// pastes into a Claude Code (or other MCP client) config to reach <c>/mcp</c> over
/// LAN/Tailscale. Login JWTs are too short-lived for a static <c>Authorization</c>
/// header, so this issues one with the configured <c>mcp.tokenLifetimeDays</c> lifetime
/// (default 90), scoped <c>read</c> (default) or <c>operate</c>, via the same
/// <see cref="JwtTokenService"/> - so it validates through the existing JwtBearer
/// middleware unchanged. Admin-gated + audited. See docs/mcp-design.md § Deployment.
/// </summary>
/// <remarks>
/// The minted token is a stateless JWT, so it cannot be individually revoked. What DOES
/// revoke it: setting <c>mcp.enabled</c> (or <c>mcp.sse.enabled</c>) false, which unmaps
/// <c>/mcp</c> and leaves every MCP token inert; letting it expire; or
/// <c>pdn auth rotate-signing-key</c> + a restart, which replaces the node's signing key and
/// invalidates ALL tokens (see <c>Cli.PdnAuthCli</c> - the verb the docs promised and nothing
/// implemented until review item C056). That's the tradeoff of a static header credential;
/// the <c>read</c> default + a bounded lifetime keep the blast radius small. Fine-grained
/// (per-token) revocation would need a jti denylist store - a hardening follow-up.
/// </remarks>
public static class PdnMcpApi
{
    /// <summary>Request body for the mint endpoint. <c>scope</c> is <c>read</c> (default) or <c>operate</c>.</summary>
    public sealed record McpTokenRequest(string? Scope);

    /// <summary>The minted token + its absolute expiry + the granted scope.</summary>
    public sealed record McpTokenResponse(string Token, DateTimeOffset ExpiresAt, string Scope, string TokenType);

    /// <summary>Map the MCP management endpoints under <c>/api/v1</c>, admin-gated.</summary>
    public static void MapPdnMcpApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);

        v1.MapPost("/mcp/token", (
            McpTokenRequest? body, HttpContext ctx, IConfigProvider config,
            IAuditLog audit, TimeProvider clock, [FromServices] JwtTokenService? tokens) =>
        {
            // The signing key (hence JwtTokenService) is unavailable if pdn.db couldn't
            // produce one - there's then nothing to sign with.
            if (tokens is null)
            {
                return Results.Problem(
                    "Auth is not configured (no signing key); cannot mint an MCP token.", statusCode: 503);
            }

            string scope = (body?.Scope ?? "read").Trim().ToLowerInvariant() switch
            {
                "operate" => AuthScopes.Operate,
                "read" or "" => AuthScopes.Read,
                _ => "",
            };
            if (scope.Length == 0)
            {
                return Results.BadRequest(new { error = "scope must be 'read' or 'operate'." });
            }

            int days = Math.Clamp(config.Current.Mcp.TokenLifetimeDays, 1, 3650);
            // The token's subject is the MINTING user (mcp:<username>), so it must be the real
            // one: with inbound claim mapping on, this read "owner" for every authenticated
            // admin and every node's tokens were indistinguishable (C011).
            string actor = PrincipalName.Or(ctx.User, "owner");
            // MCP audience: this token reaches /mcp only, never the wider control API.
            var (token, expires) = tokens.Issue($"mcp:{actor}", scope, TimeSpan.FromDays(days), JwtTokenService.McpAudience);

            audit.Record(AuditEntry.New(
                clock.GetUtcNow(), actor, "rest", "mint_mcp_token", scope, "ok",
                $"lifetimeDays={days}", ctx.Connection.RemoteIpAddress?.ToString()));

            return Results.Ok(new McpTokenResponse(token, expires, scope, "Bearer"));
        });
    }
}
