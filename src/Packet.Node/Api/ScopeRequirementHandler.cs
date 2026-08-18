using Microsoft.AspNetCore.Authorization;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// Evaluates a <see cref="ScopeRequirement"/> against the request's authenticated
/// principal - but <b>only when <c>management.auth.enabled</c> is on</b>. With the
/// flag off it succeeds the requirement unconditionally, so every gated endpoint
/// serves unauthenticated exactly as before auth existed (the default-off,
/// no-regression contract).
/// </summary>
/// <remarks>
/// The flag is read live from the <see cref="IConfigProvider"/> on each
/// authorization decision (so the policy registration is independent of the flag's
/// value and a config edit takes effect without re-wiring). When enabled, the
/// principal must be authenticated, its <c>scope</c> claim must
/// <see cref="AuthScopes.Satisfies"/> the endpoint's required scope (admin ⊃
/// operate ⊃ read), and its <c>aud</c> must match the gate's required audience
/// (control-API vs MCP) - so an MCP token never satisfies a control-API gate.
/// </remarks>
public sealed class ScopeRequirementHandler(IConfigProvider config) : AuthorizationHandler<ScopeRequirement>
{
    private readonly IConfigProvider config = config;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Default-off pass-through: auth disabled ⇒ no enforcement at all.
        if (!config.Current.Management.Auth.Enabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Enabled: require an authenticated principal whose granted scope satisfies
        // the endpoint's required scope. Anything short of that leaves the
        // requirement unmet → the framework returns 401 (no/invalid token) or 403
        // (authenticated but insufficient scope).
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var granted = user.FindFirst(AuthScopes.ScopeClaim)?.Value;
            // The token must also carry the audience this gate pins (control-API vs MCP),
            // so a token minted for one surface can't be replayed against the other.
            bool audienceMatches = user.FindAll("aud")
                .Any(c => string.Equals(c.Value, requirement.RequiredAudience, StringComparison.Ordinal));
            if (audienceMatches && AuthScopes.Satisfies(granted, requirement.RequiredScope))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
