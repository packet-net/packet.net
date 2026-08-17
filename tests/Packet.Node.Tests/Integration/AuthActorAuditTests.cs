using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Who did it, and is it written down? Two review items on one booted node:
/// <list type="bullet">
/// <item>C011 - under JwtBearer's default inbound-claim mapping the subject was renamed before
/// the identity was built, so <c>Identity.Name</c> and <c>sub</c> were both null and every
/// audited REST write was attributed to <c>owner</c> while the MCP mint minted
/// <c>mcp:owner</c>. Only the auth-OFF paths were covered, so nothing caught it.</item>
/// <item>C058 - claiming the node (<c>POST /setup</c>) and the security-relevant auth events
/// emitted nothing into the persisted audit log an owner reads.</item>
/// </list>
/// </summary>
[Trait("Category", "Node")]
public sealed class AuthActorAuditTests : IDisposable
{
    private readonly AuthNode node = new("authactor");

    [Fact]
    public async Task An_audited_write_and_a_minted_mcp_token_both_name_the_logged_in_user()
    {
        node.WriteConfig(authEnabled: false);
        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "sysop", "sysoppassword");
            await AuthNode.FlipAuthOn(setupClient);
        }

        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        var token = await AuthNode.Login(client, "sysop", "sysoppassword");

        // An audited REST write, performed as sysop.
        var write = await AuthNode.PutYaml(client, token, "/api/v1/config/raw",
            AuthNode.ConfigYaml(authEnabled: true).Replace("alias: LONDON", "alias: EDITED", StringComparison.Ordinal));
        write.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await ReadAudit(client, token);
        var row = audit.First(e => e.GetProperty("action").GetString() == "PUT /config/raw");
        row.GetProperty("actor").GetString().Should().Be("sysop");   // was "owner"

        // The MCP bearer's subject is the minting user, and it is what the audit trail of any
        // MCP tool call will then show.
        var mint = await AuthNode.PostJson(client, token, "/api/v1/mcp/token", new { scope = "read" });
        mint.StatusCode.Should().Be(HttpStatusCode.OK);
        var minted = await mint.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        var claims = AuthNode.DecodeJwtPayload(minted.GetProperty("token").GetString()!);
        claims.GetProperty("sub").GetString().Should().Be("mcp:sysop");   // was "mcp:owner"

        var mintRow = (await ReadAudit(client, token)).First(e => e.GetProperty("action").GetString() == "mint_mcp_token");
        mintRow.GetProperty("actor").GetString().Should().Be("sysop");
    }

    [Fact]
    public async Task Claiming_the_node_and_a_rejected_second_claim_are_both_audited()
    {
        node.WriteConfig(authEnabled: false);
        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        await AuthNode.Setup(client, "sysop", "sysoppassword");

        // A second claim is refused - and the refusal is the interesting forensic event.
        var second = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "G9ZZZ-9" },
            admin = new { username = "intruder", password = "password1234" },
        }, AuthNode.Web);
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Auth is off here, so /audit is ungated (the same pass-through as every other gate).
        var audit = await ReadAudit(client, token: null);
        var setups = audit.Where(e => e.GetProperty("action").GetString() == "setup").ToList();

        setups.Should().Contain(e =>
            e.GetProperty("outcome").GetString() == "ok"
            && e.GetProperty("actor").GetString() == "sysop");
        setups.Should().Contain(e =>
            e.GetProperty("outcome").GetString() == "denied"
            && e.GetProperty("actor").GetString() == "intruder");
        setups.Should().OnlyContain(e => e.GetProperty("source").GetString() == "auth");
    }

    [Fact]
    public async Task A_failed_login_is_recorded_in_the_persisted_audit_log()
    {
        node.WriteConfig(authEnabled: false);
        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        await AuthNode.Setup(client, "sysop", "sysoppassword");

        var bad = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "not-the-password" }, AuthNode.Web);
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var audit = await ReadAudit(client, token: null);
        audit.Should().Contain(e =>
            e.GetProperty("action").GetString() == "login_failed"
            && e.GetProperty("actor").GetString() == "sysop"
            && e.GetProperty("outcome").GetString() == "denied");
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadAudit(HttpClient client, string? token)
    {
        var resp = await AuthNode.Get(client, token, "/api/v1/audit");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await resp.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        return [.. rows.EnumerateArray()];
    }

    public void Dispose() => node.Dispose();
}
