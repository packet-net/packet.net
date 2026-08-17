using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The API session identity carries the engine's FULL key (packet-net/packet.net#723 item 5).
/// The AX.25 engine keys a session on <c>(Local, Remote)</c> precisely because one station can
/// hold a link to the node console and another to an application callsign at the same moment;
/// the API id carried the remote only, so those two circuits appeared as two <c>/sessions</c>
/// rows with the SAME id and <c>DELETE</c> / <c>send</c> hit whichever enumerated first - you
/// could drop the BBS link when you meant the console.
/// </summary>
/// <remarks>
/// Both circuits are opened FROM the node (one as its own callsign, one as the bound application
/// callsign) to a single station on the channel, which is the cleanest way to produce the
/// two-links-one-station state on an in-memory bus: the far station's listener keys them apart
/// exactly as the node's does.
/// </remarks>
[Trait("Category", "Node")]
public sealed class SessionIdentityApiTests : IDisposable
{
    private static readonly Callsign NodeCall = new("M0LTE", 1);
    private static readonly Callsign AppCall = new("M0LTE", 4);
    private static readonly Callsign Station = new("GB7RDG", 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string dir;

    public SessionIdentityApiTests()
    {
        dir = TestPaths.NewPath("packetnet-sessionid");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: serial-kiss
                  device: /dev/pty-vhf
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

    private sealed class NodeAppFactory(SharedRadioBus bus) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.ConfigureTestServices(services => services.AddSingleton<ITransportFactory>(
                new FakeTransportFactory().Provide("serial-kiss:/dev/pty-vhf", bus.Attach())));
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
    public async Task One_station_on_the_console_and_on_an_app_callsign_is_two_rows_with_two_ids()
    {
        var bus = new SharedRadioBus();
        await using var station = new EchoStation(bus.Attach(), Station, reply: "HI\r");
        await station.StartAsync();

        await using var factory = new NodeAppFactory(bus);
        using var client = factory.CreateClient();
        await WaitForPortUpAsync(client);

        var host = factory.Services.GetRequiredService<NodeHostedService>();
        var supervisor = host.Supervisor!;

        // The node answers for an application callsign as well as its own, and opens one
        // circuit to the same station as EACH identity.
        using var app = supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) => Task.CompletedTask);
        await supervisor.ResolveConnector("vhf")!.ConnectAsync(Station);
        await supervisor.ResolveConnector("vhf", AppCall)!.ConnectAsync(Station);

        var sessions = await client.GetFromJsonAsync<JsonElement>("/api/v1/sessions", Web);
        var rows = sessions.EnumerateArray()
            .Where(s => s.GetProperty("peer").GetString() == Station.ToString())
            .ToArray();

        rows.Should().HaveCount(2, "the same station holds two circuits, to two different local callsigns");
        var ids = rows.Select(r => r.GetProperty("id").GetString()!).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().Contain($"vhf:{Station}", "a circuit to the node's own callsign keeps the short id");
        ids.Should().Contain($"vhf:{Station}>{AppCall}", "a circuit to an app callsign carries the local half");
        rows.Select(r => r.GetProperty("local").GetString()).Should()
            .BeEquivalentTo([NodeCall.ToString(), AppCall.ToString()]);

        // DELETE the APP circuit by its full id. The console circuit must survive: resolving by
        // remote alone would have torn down whichever enumerated first.
        var drop = await client.DeleteAsync($"/api/v1/sessions/vhf:{Station}>{AppCall}");
        drop.StatusCode.Should().Be(HttpStatusCode.NoContent);

        string[] live = [];
        for (int i = 0; i < 200 && (live.Length != 1 || live[0] != $"vhf:{Station}"); i++)
        {
            await Task.Delay(50);
            var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/sessions", Web);
            live = [.. after.EnumerateArray()
                .Where(s => s.GetProperty("peer").GetString() == Station.ToString())
                .Select(s => s.GetProperty("id").GetString()!)];
        }
        // Only the app circuit went away; the console circuit to the same station survived.
        live.Should().Equal($"vhf:{Station}");
    }

    [Fact]
    public async Task A_send_addressed_to_one_of_the_two_circuits_is_accepted_by_its_own_id()
    {
        var bus = new SharedRadioBus();
        await using var station = new EchoStation(bus.Attach(), Station, reply: "HI\r");
        await station.StartAsync();

        await using var factory = new NodeAppFactory(bus);
        using var client = factory.CreateClient();
        await WaitForPortUpAsync(client);

        var supervisor = factory.Services.GetRequiredService<NodeHostedService>().Supervisor!;
        using var app = supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) => Task.CompletedTask);
        await supervisor.ResolveConnector("vhf", AppCall)!.ConnectAsync(Station);

        // The long form addresses the app circuit; the short form names a console circuit that
        // does not exist, and must NOT fall back to "any session with that remote".
        var toApp = await client.PostAsJsonAsync(
            $"/api/v1/sessions/vhf:{Station}>{AppCall}/send", new { line = "HELLO" }, Web);
        toApp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var toConsole = await client.PostAsJsonAsync(
            $"/api/v1/sessions/vhf:{Station}/send", new { line = "HELLO" }, Web);
        toConsole.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "there is no circuit to the node's own callsign with that station");
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

/// <summary>
/// The pure id convention (<see cref="SessionIds"/>) - the single definition both the web API and
/// the over-RF sysop console mint and parse through, so a <c>KICK</c> can address what
/// <c>SESSIONS</c> printed.
/// </summary>
[Trait("Category", "Node")]
public sealed class SessionIdConventionTests
{
    [Theory]
    [InlineData("vhf", "GB7RDG-1", "M0LTE-1", "M0LTE-1", "vhf:GB7RDG-1")]
    [InlineData("vhf", "GB7RDG-1", "M0LTE-4", "M0LTE-1", "vhf:GB7RDG-1>M0LTE-4")]
    [InlineData("link-dn", "G8PZT-7", null, "M0LTE-1", "link-dn:G8PZT-7")]
    [InlineData("link-dn", "G8PZT-7", "M0LTE-1", null, "link-dn:G8PZT-7")]
    public void Formats_the_short_form_only_for_the_nodes_own_callsign(
        string port, string remote, string? local, string? nodeCall, string expected)
        => SessionIds.Format(port, remote, local, nodeCall).Should().Be(expected);

    [Theory]
    [InlineData("vhf:GB7RDG-1", "vhf", "GB7RDG-1", null)]
    [InlineData("vhf:GB7RDG-1>M0LTE-4", "vhf", "GB7RDG-1", "M0LTE-4")]
    [InlineData("link-dn:M0LTE", "link-dn", "M0LTE", null)]
    [InlineData("a:b>c", "a", "b", "c")]
    public void Parses_both_forms(string id, string port, string remote, string? local)
    {
        SessionIds.TryParse(id, out var p, out var r, out var l).Should().BeTrue();
        p.Should().Be(port);
        r.Should().Be(remote);
        l.Should().Be(local);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noColon")]
    [InlineData(":leadingColon")]
    [InlineData("trailingColon:")]
    [InlineData("vhf:>M0LTE-4")]
    [InlineData("vhf:GB7RDG-1>")]
    public void Rejects_anything_it_never_mints(string id)
        => SessionIds.TryParse(id, out _, out _, out _).Should().BeFalse();
}
