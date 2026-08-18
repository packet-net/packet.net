using Microsoft.AspNetCore.Authorization;
using Packet.Node.Core.Auth;

namespace Packet.Node.Api;

/// <summary>
/// The web control-API authorization policies and the scope requirement behind
/// them - the per-endpoint gates that enforce the <c>read</c>/<c>operate</c>/<c>admin</c>
/// scopes when <c>management.auth.enabled</c> is on, and pass through entirely when
/// it is off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-off, no regression.</b> The whole gate hinges on
/// <see cref="ScopeRequirementHandler"/>: when auth is disabled it <em>succeeds
/// unconditionally</em> (no token, no claim, no challenge), so a node with auth off
/// serves every endpoint exactly as it did before auth existed. Enforcement only
/// engages when the flag is on. The flag is read live from the config provider on
/// each request, so flipping it does not require re-registering policies.
/// </para>
/// <para>
/// <b>Scope implication (admin ⊃ operate ⊃ read)</b> is applied here via
/// <see cref="AuthScopes.Satisfies"/> - a single <c>scope</c> claim on the token is
/// compared by rank against the endpoint's required scope, so an <c>admin</c> token
/// passes a <c>read</c> gate without carrying a <c>read</c> claim.
/// </para>
/// </remarks>
public static class PdnAuthPolicies
{
    /// <summary>Policy name for the read-scope gate.</summary>
    public const string Read = "pdn-read";

    /// <summary>Policy name for the operate-scope gate.</summary>
    public const string Operate = "pdn-operate";

    /// <summary>Policy name for the admin-scope gate.</summary>
    public const string Admin = "pdn-admin";

    /// <summary>Policy name for the MCP endpoint gate - read scope, but pinned to the
    /// MCP token audience so a control-API token can't reach <c>/mcp</c> and (more
    /// importantly) an MCP token can't reach the control API. Per-tool step-up to
    /// <c>operate</c> happens inside the MCP write tools.</summary>
    public const string Mcp = "pdn-mcp";

    /// <summary>Register the scope policies on the options. The control-API gates pin
    /// the control-API audience; the MCP gate pins the MCP audience - so the two token
    /// audiences stay segregated even though both validate on the same signing key.</summary>
    public static void AddPdnScopePolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(Read, p => p.AddRequirements(new ScopeRequirement(AuthScopes.Read, JwtTokenService.Audience)));
        options.AddPolicy(Operate, p => p.AddRequirements(new ScopeRequirement(AuthScopes.Operate, JwtTokenService.Audience)));
        options.AddPolicy(Admin, p => p.AddRequirements(new ScopeRequirement(AuthScopes.Admin, JwtTokenService.Audience)));
        options.AddPolicy(Mcp, p => p.AddRequirements(new ScopeRequirement(AuthScopes.Read, JwtTokenService.McpAudience)));
    }
}

/// <summary>An endpoint's required scope (one of <see cref="AuthScopes"/>) and the JWT
/// audience the satisfying token must carry (control-API vs MCP) - so a token minted for
/// one surface is not accepted on the other.</summary>
public sealed class ScopeRequirement(string requiredScope, string requiredAudience) : IAuthorizationRequirement
{
    /// <summary>The scope this endpoint requires.</summary>
    public string RequiredScope { get; } = requiredScope;

    /// <summary>The JWT <c>aud</c> the satisfying token must carry.</summary>
    public string RequiredAudience { get; } = requiredAudience;
}
