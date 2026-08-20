using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The safety net the route table never had (review item C109): walk every mapped
/// <c>/api/v1</c> endpoint and fail if one carries no authorization policy unless it is on an
/// explicit anonymous allow-list.
/// </summary>
/// <remarks>
/// There is no <c>FallbackPolicy</c> and no <c>DefaultPolicy</c> in the composition root, so a
/// future endpoint mapped without <c>.RequireAuthorization(...)</c> is anonymous on an auth-ON
/// node and nothing says so. Nothing is currently ungated - this test exists to keep it that
/// way, and to make adding a deliberately-open route a conscious edit of the list below.
/// </remarks>
[Trait("Category", "Node")]
public sealed class EndpointAuthorizationSweepTests : IDisposable
{
    /// <summary>
    /// The routes that are open BY DESIGN, with the reason. Everything else under
    /// <c>/api/v1</c> must be gated.
    /// </summary>
    private static readonly Dictionary<string, string> AnonymousByDesign = new(StringComparer.Ordinal)
    {
        ["api/v1/setup/state"] = "the first-run probe, before any account exists",
        ["api/v1/setup"] = "the first-run bootstrap itself (one-shot; refuses once a user exists)",
        ["api/v1/setup/devices"] = "the wizard's modem picker (same one-shot gate as /setup: 403 once a user exists)",
        ["api/v1/auth/login"] = "you cannot present a token to get a token",
        ["api/v1/auth/refresh"] = "the access token has, by definition, expired",
        ["api/v1/auth/logout"] = "idempotent teardown of a session whose token may be gone",
        ["api/v1/auth/webauthn/assert/begin"] = "a passwordless login carries no bearer",
        ["api/v1/auth/webauthn/assert/complete"] = "a passwordless login carries no bearer",
        ["api/{**rest}"] = "the unknown-/api/* 404 catch-all (must not fall through to the SPA)",
    };

    private readonly AuthNode node = new("endpointsweep");

    [Fact]
    public async Task Every_api_route_is_gated_unless_it_is_anonymous_by_design()
    {
        node.WriteConfig(authEnabled: false);
        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "admin", "adminpassword");
            await AuthNode.FlipAuthOn(setupClient);
        }

        await using var factory = node.Factory();
        using var client = factory.CreateClient();   // forces the host to build the route table

        // Every registered data source, flattened: WebApplication keeps the routes it maps in
        // its own source, which the composite singleton does not necessarily aggregate under
        // the test host.
        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => Route(e).StartsWith("api/", StringComparison.Ordinal))
            .ToList();

        endpoints.Should().NotBeEmpty("the sweep is worthless if it walks an empty route table");

        var ungated = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Route)
            .Distinct(StringComparer.Ordinal)
            .Where(raw => !AnonymousByDesign.ContainsKey(raw))
            .OrderBy(raw => raw, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "every /api/v1 route must carry an authorization policy - add the route to "
            + "AnonymousByDesign (with a reason) only if it is deliberately open: "
            + string.Join(", ", ungated));

        // The allow-list must not rot: every entry has to correspond to a route that still
        // exists and is still ungated, or it is silently permitting nothing (or worse, hiding
        // a rename).
        var openRoutes = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Route)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (route, why) in AnonymousByDesign)
        {
            openRoutes.Should().Contain(route, $"the allow-list still claims '{route}' is open ({why})");
        }

        // And the gate really is live on this host: an anonymous read is 401.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The route pattern without its leading slash, so the allow-list reads as plain paths.
    private static string Route(RouteEndpoint endpoint) =>
        (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    public void Dispose() => node.Dispose();
}
