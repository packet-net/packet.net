using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Core;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// A connect-out that names NO port still routes over NET/ROM (#727 item 1).
/// </summary>
/// <remarks>
/// <para>
/// #694's C060 fix gave a named <c>portId</c> its honest meaning - a DIRECT AX.25 dial on that
/// port, never NET/ROM-wrapped, exactly like the console's <c>C &lt;port&gt; &lt;call&gt;</c>.
/// The consequence nobody checked is that omitting <c>portId</c> became the ONLY way to reach a
/// NET/ROM alias, and the web panel's connect dialog always sent one (it forced the node's first
/// live port and disabled Connect until it had), so at node-v0.41.0 every NET/ROM connect from
/// the panel went out as a raw SABM on an RF port the far station was not on and 504'd after the
/// 30 s dial timeout.
/// </para>
/// <para>
/// This pins the server half of the contract the panel now depends on: with NET/ROM connect
/// routing on, the default connector (the one a body with no <c>portId</c> resolves) IS
/// NET/ROM-wrapped, and a named port's connector is NOT. <see cref="ConnectPortSelectionTests"/>
/// pins the other half - that a body with no <c>portId</c> really does dial the default
/// connector.
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class ConnectNetRomRoutingTests : IDisposable
{
    private static readonly Callsign Target = new("GB7RDG", 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string dir;

    public ConnectNetRomRoutingTests()
    {
        dir = TestPaths.NewPath("packetnet-connect-netrom");
        Directory.CreateDirectory(dir);
    }

    // One enabled port, and NET/ROM routing set by the caller. `routing: Endpoint` is the
    // minimum that makes NetRomService.ConnectEnabled true (Enabled && Endpoint-or-Transit).
    private void WriteConfig(string routing)
    {
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
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
            netRom:
              enabled: true
              routing: {routing}
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

    private sealed class NodeAppFactory(SharedRadioBus alpha) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.ConfigureTestServices(services => services.AddSingleton<ITransportFactory>(
                new FakeTransportFactory().Provide("serial-kiss:/dev/pty-alpha", alpha.Attach())));
    }

    private static async Task WaitForPortUpAsync(HttpClient client)
    {
        for (int i = 0; i < 200; i++)
        {
            var ports = await client.GetFromJsonAsync<JsonElement>("/api/v1/ports", Web);
            if (ports.EnumerateArray().Any(p => p.GetProperty("state").GetString() == "up"))
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("the port never came up");
    }

    [Fact]
    public async Task With_netrom_connect_routing_on_a_body_with_no_port_resolves_the_netrom_wrapped_connector()
    {
        WriteConfig("Endpoint");
        var alpha = new SharedRadioBus();
        await using var onAlpha = new EchoStation(alpha.Attach(), Target, reply: "ALPHA\r");
        await onAlpha.StartAsync();

        await using var factory = new NodeAppFactory(alpha);
        using var client = factory.CreateClient();
        await WaitForPortUpAsync(client);

        var supervisor = factory.Services.GetRequiredService<NodeHostedService>().Supervisor;
        supervisor.Should().NotBeNull();

        // The default connector - what a POST /sessions body with no portId resolves.
        supervisor!.ResolveDefaultConnector().Should().BeOfType<NetRomOutboundConnector>(
            "with connect routing on, an alias typed with no port must route across the network");

        // A named port is a direct dial, and stays one (the #694 C060 semantic).
        supervisor.ResolveConnector("alpha").Should().NotBeOfType<NetRomOutboundConnector>(
            "naming a port is a deliberate 'go out this way' and is never NET/ROM-wrapped");

        // And the auto path still connects: NetRomOutboundConnector falls back to the same-port
        // AX.25 dial for a target with no NET/ROM route, so a local station is reachable too.
        var resp = await client.PostAsJsonAsync("/api/v1/sessions", new { target = Target.ToString() }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("portId").GetString().Should().Be("alpha");
        onAlpha.SawConnect.Should().BeTrue();
    }

    [Fact]
    public async Task With_netrom_routing_off_the_default_connector_is_a_plain_ax25_dial()
    {
        WriteConfig("None");
        var alpha = new SharedRadioBus();
        await using var onAlpha = new EchoStation(alpha.Attach(), Target, reply: "ALPHA\r");
        await onAlpha.StartAsync();

        await using var factory = new NodeAppFactory(alpha);
        using var client = factory.CreateClient();
        await WaitForPortUpAsync(client);

        var supervisor = factory.Services.GetRequiredService<NodeHostedService>().Supervisor;
        supervisor!.ResolveDefaultConnector().Should().NotBeOfType<NetRomOutboundConnector>(
            "there is no routing to do, so there is nothing to wrap");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
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
