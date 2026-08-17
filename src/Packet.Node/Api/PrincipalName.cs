using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Packet.Node.Api;

/// <summary>
/// The one place the node resolves "who is this request?" from a
/// <see cref="ClaimsPrincipal"/>. Every audit actor, MCP token subject, identity header
/// and self-service username goes through here so they can never disagree again.
/// </summary>
/// <remarks>
/// <para>
/// The node's JWTs carry the username in <c>sub</c> (<c>JwtTokenService.Issue</c>) and the
/// validation parameters set <c>NameClaimType = sub</c>, so <see cref="IIdentity.Name"/> is
/// the username - <b>provided</b> the bearer handler is not renaming inbound claims.
/// <c>JwtBearerOptions.MapInboundClaims</c> defaults to <c>true</c>, which rewrites <c>sub</c>
/// to <see cref="ClaimTypes.NameIdentifier"/> before the identity is built, leaving both
/// <c>Name</c> and the <c>sub</c> lookup null; that is how every audited REST write was
/// attributed to "owner" and the MCP mint minted <c>mcp:owner</c> (review item C011). The
/// root fix is <c>MapInboundClaims = false</c> in the composition root; this helper is the
/// belt to that pair of braces, and it makes any future handler (or a test principal built
/// by hand) resolve the same way.
/// </para>
/// </remarks>
internal static class PrincipalName
{
    /// <summary>The authenticated subject's username, or <c>null</c> when the principal is
    /// absent, unauthenticated, or carries no subject claim under any of its three spellings
    /// (<c>Name</c> / <c>sub</c> / <c>nameidentifier</c>).</summary>
    internal static string? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var name = principal.Identity.Name
            ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// <see cref="Resolve"/> with a caller-chosen stand-in for "no authenticated subject" -
    /// <c>owner</c> where the unauthenticated caller IS the node owner (auth off, local
    /// operator) and <c>anonymous</c> where it is merely unknown.
    /// </summary>
    internal static string Or(ClaimsPrincipal? principal, string fallback) =>
        Resolve(principal) ?? fallback;
}
