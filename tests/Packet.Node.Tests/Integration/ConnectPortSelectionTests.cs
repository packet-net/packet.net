using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Core;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Which port a web connect-out actually dials (review item C060, #694). <c>POST
/// /api/v1/sessions</c> validated <c>portId</c> and then dialled the supervisor's
/// <em>default</em> connector regardless, so on a two-port node the SABM left on the
/// ordinal-first port whatever the operator chose in the UI's "Via port" picker.
/// </summary>
/// <remarks>
/// Two ports on two separate in-memory buses, an <see cref="EchoStation"/> answering on each,
/// so the dial completes fast and the station that saw the SABM identifies the port it left on.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ConnectPortSelectionTests : IDisposable
{
    private static readonly Callsign Target = new("GB7RDG", 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string dir;

    public ConnectPortSelectionTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-connectport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");

        // Two enabled ports. Ordinal id order makes "alpha" the default connector's port, so a
        // dial that ignores portId lands there - which is exactly the bug.
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
            ports:
              - id: alpha
                enabled: true
                transport:
                  kind: serial-kiss
                  device: /dev/pty-alpha
              - id: bravo
                enabled: true
                transport:
                  kind: serial-kiss
                  device: /dev/pty-bravo
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory(SharedRadioBus alpha, SharedRadioBus bravo) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.ConfigureTestServices(services => services.AddSingleton<ITransportFactory>(
                new FakeTransportFactory()
                    .Provide("serial-kiss:/dev/pty-alpha", alpha.Attach())
                    .Provide("serial-kiss:/dev/pty-bravo", bravo.Attach())));
    }

    private static async Task WaitForPortsUpAsync(HttpClient client)
    {
        for (int i = 0; i < 200; i++)
        {
            var ports = await client.GetFromJsonAsync<JsonElement>("/api/v1/ports", Web);
            if (ports.EnumerateArray().Count(p => p.GetProperty("state").GetString() == "up") == 2)
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("the two ports never came up");
    }

    [Fact]
    public async Task A_named_port_is_the_port_the_dial_leaves_on()
    {
        var alpha = new SharedRadioBus();
        var bravo = new SharedRadioBus();
        await using var onAlpha = new EchoStation(alpha.Attach(), Target, reply: "ALPHA\r");
        await using var onBravo = new EchoStation(bravo.Attach(), Target, reply: "BRAVO\r");
        await onAlpha.StartAsync();
        await onBravo.StartAsync();

        await using var factory = new NodeAppFactory(alpha, bravo);
        using var client = factory.CreateClient();
        await WaitForPortsUpAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/sessions",
            new { target = Target.ToString(), portId = "bravo" }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        info.GetProperty("portId").GetString().Should().Be("bravo");
        info.GetProperty("id").GetString().Should().StartWith("bravo:");

        onBravo.SawConnect.Should().BeTrue("the SABM went out on the port the caller named");
        onAlpha.SawConnect.Should().BeFalse("and NOT on the ordinal-first default port");
    }

    [Fact]
    public async Task No_port_named_still_dials_the_default_connector()
    {
        var alpha = new SharedRadioBus();
        var bravo = new SharedRadioBus();
        await using var onAlpha = new EchoStation(alpha.Attach(), Target, reply: "ALPHA\r");
        await using var onBravo = new EchoStation(bravo.Attach(), Target, reply: "BRAVO\r");
        await onAlpha.StartAsync();
        await onBravo.StartAsync();

        await using var factory = new NodeAppFactory(alpha, bravo);
        using var client = factory.CreateClient();
        await WaitForPortsUpAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/sessions", new { target = Target.ToString() }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        info.GetProperty("portId").GetString().Should().Be("alpha", "the default connector is the first port by id order");
        onAlpha.SawConnect.Should().BeTrue();
        onBravo.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_port_is_still_a_404_and_dials_nothing()
    {
        var alpha = new SharedRadioBus();
        var bravo = new SharedRadioBus();
        await using var onAlpha = new EchoStation(alpha.Attach(), Target, reply: "ALPHA\r");
        await onAlpha.StartAsync();

        await using var factory = new NodeAppFactory(alpha, bravo);
        using var client = factory.CreateClient();
        await WaitForPortsUpAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/sessions",
            new { target = Target.ToString(), portId = "ghost" }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        onAlpha.SawConnect.Should().BeFalse("a bad port name must not silently fall back to the default");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
