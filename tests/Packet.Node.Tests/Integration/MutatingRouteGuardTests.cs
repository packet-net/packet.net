using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The mutating routes whose guard branches no test had ever taken (review item C108), plus
/// the console-id gate on the operate-scoped session routes (review item C064).
/// </summary>
/// <remarks>
/// Route greps over <c>tests/</c> found zero request sites for <c>DELETE /users/{u}</c> (so the
/// last-admin 409 and both 404s were entirely unexercised), <c>/oauth/revoke</c>,
/// <c>DELETE /auth/webauthn/credentials/{id}</c> and the app identity write. Each one below is
/// a guard an operator relies on.
/// </remarks>
[Trait("Category", "Node")]
public sealed class MutatingRouteGuardTests : IDisposable
{
    // The validator refuses mcp.oauth.enabled without management.auth.enabled (an open
    // authorization server on an unauthenticated node is nonsense), so the OAuth leg boots
    // auth-ON like the rest of this suite.
    private const string OauthBlock = """
        mcp:
          enabled: false
          oauth:
            enabled: true
        """;

    private readonly AuthNode node = new("routeguards");
    private AuthNode.NodeAppFactory? factory;

    [Fact]
    public async Task Deleting_users_honours_the_last_admin_guard_and_the_unknown_user_404()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");

        // Unknown user → 404 (before any state changes).
        (await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/users/nobody", adminToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The only admin cannot delete itself into a locked-out node.
        (await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/users/admin", adminToken))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // A second admin lifts the guard for the first.
        await AuthNode.CreateUser(client, adminToken, "admin2", "admin2password", "admin");
        (await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/users/admin2", adminToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A non-admin cannot delete anyone.
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");
        (await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/users/operator", operateToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_console_session_id_is_not_reachable_through_the_operate_scoped_session_routes()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");

        // Open a real node console (admin-gated, its own route family).
        var open = await AuthNode.PostJson(client, adminToken, "/api/v1/console", new { });
        open.StatusCode.Should().Be(HttpStatusCode.OK);
        var consoleId = (await open.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web))
            .GetProperty("id").GetString()!;
        consoleId.Should().StartWith("console:");

        // The session routes are `operate` and address AX.25 sessions. The manager holds both
        // kinds in one dictionary, so an operate caller used to reach the node's command shell
        // through them.
        var send = await AuthNode.PostJson(client, operateToken, $"/api/v1/sessions/{consoleId}/send", new { line = "PORTS" });
        send.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stream = await AuthNode.Get(client, operateToken, $"/api/v1/sessions/{consoleId}/stream");
        stream.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var kill = await AuthNode.Send(client, HttpMethod.Delete, $"/api/v1/sessions/{consoleId}", operateToken);
        kill.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An admin still reaches it (the id is real - the operate 404s above are the gate, not
        // a missing session).
        var adminSend = await AuthNode.PostJson(client, adminToken, $"/api/v1/sessions/{consoleId}/send", new { line = "PORTS" });
        adminSend.StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await AuthNode.Send(client, HttpMethod.Delete, $"/api/v1/console/{consoleId}", adminToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_passkey_can_only_be_deleted_by_its_owner_and_an_unknown_id_is_404()
    {
        using var client = await BootAuthOn();
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");

        var credentials = new SqliteWebAuthnCredentialStore(node.DbPath, NullLogger<SqliteWebAuthnCredentialStore>.Instance);
        var mine = new byte[] { 7, 7, 7 };
        var someoneElses = new byte[] { 8, 8, 8 };
        credentials.Add(Record(mine, "operator")).Should().BeTrue();
        credentials.Add(Record(someoneElses, "admin")).Should().BeTrue();

        // Unknown id → 404.
        (await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/auth/webauthn/credentials/AAAA", operateToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Someone else's credential → 404 (the store's ownership predicate), never 204.
        (await AuthNode.Send(client, HttpMethod.Delete, $"/api/v1/auth/webauthn/credentials/{Base64Url(someoneElses)}", operateToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        credentials.GetByCredentialId(someoneElses).Should().NotBeNull();

        // Their own → 204, and it is really gone.
        (await AuthNode.Send(client, HttpMethod.Delete, $"/api/v1/auth/webauthn/credentials/{Base64Url(mine)}", operateToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        credentials.GetByCredentialId(mine).Should().BeNull();

        // The deletion of a login credential is in the persisted audit log (C058).
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var audit = await AuthNode.Get(client, adminToken, "/api/v1/audit");
        var rows = await audit.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        rows.EnumerateArray().Should().Contain(e =>
            e.GetProperty("action").GetString() == "passkey_deleted"
            && e.GetProperty("actor").GetString() == "operator");
    }

    [Fact]
    public async Task Setting_an_identity_on_an_unknown_app_package_is_404_and_admin_gated()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");

        var body = new { command = "WALL", callsign = "M0LTE-9", netromAlias = "WALL", netromQuality = 100 };

        (await AuthNode.PutJson(client, operateToken, "/api/v1/apps/packages/nosuch/identity", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await AuthNode.PutJson(client, adminToken, "/api/v1/apps/packages/nosuch/identity", body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Oauth_revoke_answers_rfc7009_when_enabled_and_404s_when_not()
    {
        // Default-off: the whole OAuth family is absent (not merely gated).
        node.WriteConfig(authEnabled: false);
        await using (var offFactory = node.Factory())
        using (var offClient = offFactory.CreateClient())
        {
            (await Revoke(offClient)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // Opted in (and auth on, which the validator requires): RFC 7009 says answer 200 for
        // any token, valid or not. It cannot actually kill a live MCP JWT - see
        // `pdn auth rotate-signing-key`, the only revocation a stateless token has.
        using var client = await BootAuthOn(OauthBlock);
        (await Revoke(client)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> Revoke(HttpClient client) =>
        client.PostAsync("/oauth/revoke",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", "whatever")]));

    private static WebAuthnCredentialRecord Record(byte[] id, string username) =>
        new(id, username, [1, 2, 3], SignCount: 0, CredType: "public-key",
            Transports: "internal", AaGuid: null, CreatedUtc: DateTimeOffset.UnixEpoch, LastUsedUtc: null);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<HttpClient> BootAuthOn(string? extra = null)
    {
        node.WriteConfig(authEnabled: false);

        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "admin", "adminpassword");
            var adminToken = await AuthNode.Login(setupClient, "admin", "adminpassword");
            await AuthNode.CreateUser(setupClient, adminToken, "operator", "operatorpassword", "operate");
            await AuthNode.FlipAuthOn(setupClient, extra);
        }

        factory = node.Factory();
        return factory.CreateClient();
    }

    public void Dispose()
    {
        factory?.Dispose();
        node.Dispose();
    }
}
