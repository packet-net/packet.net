using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Tests.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The keystone for packet.net#476: a <b>service</b> app reachable by its bare command verb at the
/// node prompt even when it is <b>un-migrated</b> - it bound a self-derived SSID instead of the
/// node-resolved <c>PDN_APP_CALLSIGN</c>. The node resolves <c>BBS</c> → <c>NODE-1</c> but the app
/// actually bound <c>NODE-4</c>; typing <c>BBS</c> must still land inside the app. A migrated app
/// (bound the resolved callsign) keeps working - the resolver returns the resolved callsign when it
/// is the live binding, and only bridges to a stray when it is not.
/// </summary>
[Trait("Category", "Node")]
public sealed class ServiceVerbSelfDerivedTests
{
    // The node is on the bare base (SSID 0) so the resolver auto-assigns the app the lowest free
    // SSID = 1 (NODE-1) - the node's own SSID 0 is the only one reserved.
    private static readonly Callsign NodeCall = new("NODE", 0);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
    };

    // A packet-capable service package (command verb, no session block) - the shape the node
    // resolves a loopback callsign for.
    private static AppPackageManifest ServiceManifest(string id, string verb) => new()
    {
        Manifest = 1,
        Id = id,
        Capabilities = [AppCapabilities.Packet],
        Packet = new AppPacketSpec { Command = verb },
        Service = new AppServiceSpec { Command = "/bin/sh", Args = ["run.sh"] },
    };

    [Fact]
    public async Task A_self_deriving_service_app_is_reachable_by_its_bare_verb()
    {
        // The node resolves BBS → NODE-1 (lowest free SSID), but the un-migrated app self-derived
        // NODE-4 and bound THAT. The bare verb must bridge to the bound NODE-4, not the unbound
        // NODE-1, so a caller typing "BBS" at the prompt lands inside the app.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));

        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var remoteModem = bus.Attach();

        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        var appHost = new ApplicationHost(provider, NullLoggerFactory.Instance, catalog);
        await using var supervisor = new PortSupervisor(
            provider, factory, TimeProvider.System, NullLoggerFactory.Instance, applicationHost: appHost);
        appHost.LocalAppRegistry = supervisor;   // the live binding view (#476)
        await supervisor.StartAsync();

        // The un-migrated app binds a DIFFERENT SSID than the node resolved (NODE-4, not NODE-1).
        var selfDerived = new Callsign("NODE", 4);
        var fromCaller = new ConcurrentQueue<string>();
        using var registration = supervisor.RegisterAppCallsign(selfDerived, portId: null, async (conn, _) =>
        {
            await conn.WriteAsync("BBSv1>\r"u8.ToArray());
            while (true)
            {
                var chunk = await conn.ReadAsync();
                if (chunk.IsEmpty)
                {
                    break;
                }
                fromCaller.Enqueue(Encoding.UTF8.GetString(chunk.Span));
            }
        });

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome to"), "the node console banner");

        // Type the bare verb - NOT "C NODE-4". The console resolves it to the app's bound callsign.
        remote.SendLine("BBS");

        await Wait.ForAsync(() => remote.Saw("BBSv1>"),
            "the bare verb reached the self-deriving app (bound NODE-4, not the resolved NODE-1)");

        remote.SendLine("HELLO BBS");
        await Wait.ForAsync(() => fromCaller.Any(s => s.Contains("HELLO BBS", StringComparison.Ordinal)),
            "the caller's line flows into the app over the verb-initiated loopback");
    }

    [Fact]
    public async Task A_migrated_service_app_is_still_reachable_by_its_bare_verb()
    {
        // No regression: a migrated app bound exactly its node-resolved PDN_APP_CALLSIGN (NODE-1).
        // The resolver returns the resolved callsign because it IS the live binding.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));

        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var remoteModem = bus.Attach();

        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        var appHost = new ApplicationHost(provider, NullLoggerFactory.Instance, catalog);
        await using var supervisor = new PortSupervisor(
            provider, factory, TimeProvider.System, NullLoggerFactory.Instance, applicationHost: appHost);
        appHost.LocalAppRegistry = supervisor;
        await supervisor.StartAsync();

        var resolved = new Callsign("NODE", 1);   // the node-resolved PDN_APP_CALLSIGN for BBS
        using var registration = supervisor.RegisterAppCallsign(resolved, portId: null, async (conn, _) =>
        {
            await conn.WriteAsync("BBSv1>\r"u8.ToArray());
            await conn.ReadAsync();
        });

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome to"), "the node console banner");

        remote.SendLine("BBS");

        await Wait.ForAsync(() => remote.Saw("BBSv1>"),
            "the bare verb reaches the migrated app at its node-resolved callsign");
    }
}
