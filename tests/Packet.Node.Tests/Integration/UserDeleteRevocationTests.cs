using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Deleting a user deletes the account, not just the row (review item C055).
/// </summary>
/// <remarks>
/// <c>refresh_token</c> and <c>webauthn_credential</c> are keyed by username with no foreign
/// key, and both <c>/auth/refresh</c> and the passkey assert only check that a user with that
/// name exists <em>now</em> - so a deleted user's refresh chain and passkeys survived in
/// <c>pdn.db</c> and came back to life the moment an admin recreated the same username. The
/// reproduction here is exactly that: delete, recreate, and check the old credentials are dead.
/// </remarks>
[Trait("Category", "Node")]
public sealed class UserDeleteRevocationTests : IDisposable
{
    private readonly AuthNode node = new("userdelete");

    [Fact]
    public async Task Deleting_a_user_kills_their_refresh_families_and_passkeys_even_if_the_name_returns()
    {
        node.WriteConfig(authEnabled: false);
        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        await AuthNode.Setup(client, "admin", "adminpassword");
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        await AuthNode.CreateUser(client, adminToken, "victim", "victimpassword", "read");

        // A live session (refresh token) and an enrolled passkey for the victim. The passkey
        // goes in through the store because a real ceremony needs an authenticator; the row is
        // what the assert path resolves against, and the row is what used to survive.
        var session = await AuthNode.LoginFull(client, "victim", "victimpassword");
        var refreshToken = session.GetProperty("refreshToken").GetString()!;
        var credentials = new SqliteWebAuthnCredentialStore(node.DbPath, NullLogger<SqliteWebAuthnCredentialStore>.Instance);
        var credentialId = new byte[] { 1, 2, 3, 4, 5 };
        credentials.Add(new WebAuthnCredentialRecord(
            credentialId, "victim", [9, 9, 9], SignCount: 1, CredType: "public-key",
            Transports: "internal", AaGuid: null, CreatedUtc: DateTimeOffset.UnixEpoch, LastUsedUtc: null))
            .Should().BeTrue();

        // The refresh token works while the account exists (so the negative below means
        // something).
        (await Refresh(client, refreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await AuthNode.LoginFull(client, "victim", "victimpassword");
        var liveRefresh = rotated.GetProperty("refreshToken").GetString()!;

        var delete = await AuthNode.Send(client, HttpMethod.Delete, "/api/v1/users/victim", adminToken);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The passkey is gone immediately - not merely unresolvable while the user is absent.
        credentials.GetByCredentialId(credentialId).Should().BeNull();
        credentials.GetByUser("victim").Should().BeEmpty();

        // Recreate the same username (the only "reset a password" path this node has).
        await AuthNode.CreateUser(client, adminToken, "victim", "brand-new-password", "read");

        // The old session does NOT come back with the name.
        (await Refresh(client, liveRefresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(client, refreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And the delete says what it revoked, so the audit trail records the blast radius.
        var audit = await AuthNode.Get(client, adminToken, "/api/v1/audit");
        var rows = await audit.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        var row = rows.EnumerateArray().First(e => e.GetProperty("action").GetString() == "delete_user");
        row.GetProperty("detail").GetString().Should().Contain("passkeysDeleted=1");
        row.GetProperty("detail").GetString().Should().Contain("refreshTokensRevoked=");
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken }, AuthNode.Web);

    public void Dispose() => node.Dispose();
}
