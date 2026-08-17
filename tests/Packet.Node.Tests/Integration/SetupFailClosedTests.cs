using System.Net;
using System.Net.Http.Json;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The unauthenticated first-run bootstrap fails CLOSED when the user store cannot be read
/// (review item C026).
/// </summary>
/// <remarks>
/// <c>SqliteUserStore.Count()</c> returned 0 on a store fault, commented as "the safe failure
/// mode" - but zero is precisely the open gate: <c>POST /setup</c>'s only guard is
/// <c>users.Count() > 0</c>, and it applies the station identity through the config write seam
/// BEFORE creating the user, so during a read fault an unauthenticated caller could rewrite
/// the callsign/alias of a node that already had an owner. The count now says "unknown" and
/// the endpoint answers 503.
/// </remarks>
[Trait("Category", "Node")]
public sealed class SetupFailClosedTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string unopenableDbPath;

    public SetupFailClosedTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-setupfailclosed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // A db under a path segment that is a FILE, not a directory: SQLite cannot open it, so
        // every store operation faults exactly as a corrupt/unreadable pdn.db would.
        var blocker = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(blocker, "this is a file, not a directory");
        unopenableDbPath = Path.Combine(blocker, "pdn.db");

        File.WriteAllText(configPath, AuthNode.ConfigYaml(authEnabled: false));
    }

    [Fact]
    public async Task Setup_is_503_and_the_probe_says_no_setup_when_the_user_store_cannot_be_read()
    {
        await using var factory = new AuthNode.NodeAppFactory(configPath, unopenableDbPath);
        using var client = factory.CreateClient();

        // The probe fails closed: an operator sees "no setup needed" (and then a login they
        // cannot complete) rather than a wizard that hands the node away.
        var state = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/setup/state", AuthNode.Web);
        state.GetProperty("needsSetup").GetBoolean().Should().BeFalse();

        // And the claim itself is refused loudly, before any config is applied.
        var setup = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "G9ZZZ-9", alias = "TAKEN" },
            admin = new { username = "intruder", password = "password1234" },
        }, AuthNode.Web);
        setup.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The station identity is untouched (the pre-fix path applied it before creating the
        // user, so a Count fault let an unauthenticated caller rewrite it).
        var cfg = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/config", AuthNode.Web);
        cfg.GetProperty("identity").GetProperty("callsign").GetString().Should().Be("M0LTE-1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
