using System.Net;
using System.Text.Json;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the MHeard REST surface (#454):
/// <c>GET /api/v1/heard</c> (node-wide) and <c>?port=&lt;id&gt;</c> (per-port) are mapped, reachable,
/// and return a JSON array (empty on an idle node, nothing has been heard yet). Uses the shared
/// <see cref="NodeHostFixture"/> harness.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeardApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly NodeHostFixture node = new(
        NodeYaml.Build(ports: [NodeYaml.DisabledKissTcpPort("vhf", 8141)]), "heardapi");

    [Fact]
    public async Task Heard_node_wide_and_per_port_are_served_as_arrays()
    {
        var client = node.CreateClient();

        var wide = await client.GetAsync("/api/v1/heard");
        wide.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement[]>(await wide.Content.ReadAsStringAsync(), Web)
            .Should().NotBeNull().And.BeEmpty();   // idle node: nothing heard yet

        var perPort = await client.GetAsync("/api/v1/heard?port=vhf");
        perPort.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement[]>(await perPort.Content.ReadAsStringAsync(), Web)
            .Should().NotBeNull().And.BeEmpty();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        node.Dispose();
    }
}
