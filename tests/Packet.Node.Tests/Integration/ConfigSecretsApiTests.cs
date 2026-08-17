using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The config surface's two auth-facing contracts, end to end on a booted auth-ON node:
/// secret-bearing fields never leave the node through a read-scoped endpoint (review item
/// C010), and the write scope is <c>operate</c> - except for the <c>management.auth</c> block,
/// which is <c>admin</c> because that block is the gate itself (review item C020).
/// </summary>
[Trait("Category", "Node")]
public sealed class ConfigSecretsApiTests : IDisposable
{
    private const string TailnetKey = "tskey-auth-SUPERSECRET-000";
    private const string MqttPassword = "broker-hunter2";
    private const string CertPassword = "pkcs12-hunter2";

    // One of each secret the read projection has to mask.
    private const string SecretBlocks = """
        tailscale:
          enabled: false
          authKey: tskey-auth-SUPERSECRET-000
        mqtt:
          enabled: false
          host: 127.0.0.1
          username: pdn
          password: broker-hunter2
        """;

    private const string HttpsBlock = """
          https:
            enabled: false
            certificatePath: /tmp/pdn-test-cert.pfx
            certificatePassword: pkcs12-hunter2
        """;

    private readonly AuthNode node = new("configsecrets");

    [Fact]
    public async Task A_read_scope_caller_never_sees_a_configured_secret()
    {
        var readToken = await BootAuthOnAndLogin("reader", "read");

        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        var json = await AuthNode.Get(client, readToken, "/api/v1/config");
        json.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await json.Content.ReadAsStringAsync();
        body.Should().NotContain(TailnetKey);
        body.Should().NotContain(MqttPassword);
        body.Should().NotContain(CertPassword);

        var cfg = JsonDocument.Parse(body).RootElement;
        cfg.GetProperty("tailscale").GetProperty("authKey").GetString().Should().Be("***");
        cfg.GetProperty("mqtt").GetProperty("password").GetString().Should().Be("***");
        cfg.GetProperty("management").GetProperty("https").GetProperty("certificatePassword")
            .GetString().Should().Be("***");

        // The raw-YAML read is the same surface with a different serialiser - it leaked the
        // same three values (and node-api.yaml claimed it was admin-gated while the server
        // served it at read).
        var raw = await AuthNode.Get(client, readToken, "/api/v1/config/raw");
        raw.StatusCode.Should().Be(HttpStatusCode.OK);
        var yaml = await raw.Content.ReadAsStringAsync();
        yaml.Should().NotContain(TailnetKey);
        yaml.Should().NotContain(MqttPassword);
        yaml.Should().NotContain(CertPassword);
        yaml.Should().Contain("***");
    }

    [Fact]
    public async Task A_read_modify_write_round_trip_keeps_the_stored_secrets()
    {
        var operateToken = await BootAuthOnAndLogin("operator", "operate");

        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        // Exactly what the panel does: GET the (masked) config, change something unrelated,
        // PUT the whole object back.
        var get = await AuthNode.Get(client, operateToken, "/api/v1/config");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = JsonNode.Parse(await get.Content.ReadAsStringAsync())!;
        edited["identity"]!["alias"] = "EDITED";

        var put = await AuthNode.Send(client, HttpMethod.Put, "/api/v1/config", operateToken,
            new StringContent(edited.ToJsonString(), Encoding.UTF8, "application/json"));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // The edit landed AND the store still holds the real secrets: a "***" round trip must
        // neither persist the placeholder nor wipe the value.
        var stored = new SqliteConfigStore(node.DbPath, TimeProvider.System, NullLogger<SqliteConfigStore>.Instance).Load();
        stored.Should().NotBeNull();
        stored!.Value.Config.Identity.Alias.Should().Be("EDITED");
        stored.Value.Config.Tailscale.AuthKey.Should().Be(TailnetKey);
        stored.Value.Config.Mqtt.Password.Should().Be(MqttPassword);
        stored.Value.Config.Management.Https.CertificatePassword.Should().Be(CertPassword);
    }

    [Fact]
    public async Task An_operate_caller_may_write_config_but_not_the_auth_block()
    {
        var operateToken = await BootAuthOnAndLogin("operator", "operate");

        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        // An ordinary config write is `operate` - the shipped model, and what the panel now
        // gates Review-and-apply on.
        var ordinary = await AuthNode.PutYaml(client, operateToken, "/api/v1/config/raw",
            AuthNode.ConfigYaml(true, SecretBlocks, HttpsBlock).Replace("alias: LONDON", "alias: EDITED", StringComparison.Ordinal));
        ordinary.StatusCode.Should().Be(HttpStatusCode.OK);

        // Turning auth OFF is not, or operate would silently equal admin.
        var disableAuth = await AuthNode.PutYaml(client, operateToken, "/api/v1/config/raw",
            AuthNode.ConfigYaml(false, SecretBlocks, HttpsBlock));
        disableAuth.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // ... and the node is still gated, so the rejected write really did not apply.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The same write from an admin goes through.
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var byAdmin = await AuthNode.PutYaml(client, adminToken, "/api/v1/config/raw",
            AuthNode.ConfigYaml(false, SecretBlocks, HttpsBlock));
        byAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Claim the node, add the scoped user and turn auth on - all through the API while it is
    // still ungated. Returns the scoped user's access token, minted against the same db the
    // auth-on host then boots over.
    private async Task<string> BootAuthOnAndLogin(string username, string scope)
    {
        node.WriteConfig(authEnabled: false, SecretBlocks, HttpsBlock);

        await using var setupFactory = node.Factory();
        using var setupClient = setupFactory.CreateClient();

        await AuthNode.Setup(setupClient, "admin", "adminpassword");
        var adminToken = await AuthNode.Login(setupClient, "admin", "adminpassword");
        await AuthNode.CreateUser(setupClient, adminToken, username, "userpassword", scope);
        var token = await AuthNode.Login(setupClient, username, "userpassword");

        await AuthNode.FlipAuthOn(setupClient, SecretBlocks, HttpsBlock);
        return token;
    }

    public void Dispose() => node.Dispose();
}
