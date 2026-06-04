using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The read-only "NET/ROM aware" slice, end-to-end over the in-memory radio bus:
/// a third station broadcasts a NODES routing frame (UI, PID 0xCF, dest
/// <c>NODES</c>); the node — a real <see cref="PortSupervisor"/> wired to a
/// <see cref="NetRomService"/> — hears it on the port's frame-trace tap, builds
/// a routing table, surfaces it in the <c>Nodes</c> console command, and is
/// proven unable to disturb a live QSO while doing so.
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomAwareIntegrationTests
{
    private static readonly Callsign NodeCall = new("M0NODE", 0);
    private static readonly Callsign Neighbour = new("GB7RDG", 0);     // the NODES broadcaster
    private static readonly Callsign DestSot = new("GB7SOT", 0);       // a destination it advertises
    private static readonly Callsign ViaXyz = new("GB7XYZ", 2);        // its chosen best-neighbour for SOT
    private static readonly Callsign RemoteCall = new("M0RMOT", 0);    // an actual QSO peer

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "NODE" },
        NetRom = new NetRomConfig { Enabled = true },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "mem", Port = 1 },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
    };

    [Fact]
    public async Task Node_hears_a_NODES_broadcast_and_learns_the_routes()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var broadcaster = bus.Attach();   // the third station's modem — broadcasts raw UI

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero));
        var netRom = new NetRomService(new NetRomConfig { Enabled = true }, clock, NullLogger<NetRomService>.Instance);

        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, clock, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        // GB7RDG (alias RDGBPQ) broadcasts: it can reach GB7SOT (alias SOT) via
        // GB7XYZ at quality 200. Send it as a UI frame to "NODES", PID 0xCF.
        var info = BuildNodesInfo("RDGBPQ", (DestSot, "SOT", ViaXyz, 200));
        await BroadcastNodesAsync(broadcaster, Neighbour, info);

        // The node hears it on the tap and ingests it.
        await Wait.ForAsync(() => netRom.Snapshot().NeighbourCount > 0, "the node should hear the NODES broadcast");

        var snap = netRom.Snapshot();
        snap.Neighbours.Should().ContainSingle();
        snap.Neighbours[0].Neighbour.Should().Be(Neighbour);
        snap.Neighbours[0].Alias.Should().Be("RDGBPQ");
        snap.Neighbours[0].PortId.Should().Be("p1");

        // Two destinations: the assumed direct route to GB7RDG, and GB7SOT via it.
        snap.Destinations.Should().Contain(d => d.Destination == Neighbour);
        var sot = snap.Destinations.Single(d => d.Destination == DestSot);
        sot.Alias.Should().Be("SOT");
        sot.BestRoute!.Neighbour.Should().Be(Neighbour);   // we forward to the broadcaster
    }

    [Fact]
    public async Task The_Nodes_console_command_surfaces_the_learned_routes()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var broadcaster = bus.Attach();

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero));
        var netRom = new NetRomService(new NetRomConfig { Enabled = true }, clock, NullLogger<NetRomService>.Instance);

        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, clock, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        var info = BuildNodesInfo("RDGBPQ", (DestSot, "SOT", ViaXyz, 200));
        await BroadcastNodesAsync(broadcaster, Neighbour, info);
        await Wait.ForAsync(() => netRom.Snapshot().DestinationCount >= 2, "routes should be learned");

        // A remote connects to the node and runs `N` (Nodes); the reply should now
        // carry the learned NET/ROM neighbours + routes, not just the port list.
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("NODE"), "banner should arrive on connect");

        remote.SendLine("N");
        await Wait.ForAsync(() => remote.Saw("NET/ROM neighbours:"), "Nodes should surface NET/ROM neighbours");
        remote.Saw("RDGBPQ:GB7RDG").Should().BeTrue("the heard neighbour's alias:callsign appears");
        remote.Saw("NET/ROM routes:").Should().BeTrue("Nodes should surface the learned routes");
        remote.Saw("SOT:GB7SOT").Should().BeTrue("the advertised destination appears as a route");
    }

    [Fact]
    public async Task A_NODES_broadcast_does_not_disturb_a_live_QSO()
    {
        // The read-only guarantee: a NODES broadcast arriving mid-QSO is consumed
        // by the tap only and must not perturb the connected session. We connect,
        // fire a NODES broadcast onto the shared channel, and confirm the link is
        // still Connected and still carries data afterwards.
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var broadcaster = bus.Attach();

        var netRom = new NetRomService(new NetRomConfig { Enabled = true }, TimeProvider.System, NullLogger<NetRomService>.Instance);

        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("NODE"), "banner should arrive on connect");
        remote.CurrentState.Should().Be("Connected");

        // Storm the channel with NODES broadcasts while connected.
        var info = BuildNodesInfo("RDGBPQ", (DestSot, "SOT", ViaXyz, 200));
        for (int i = 0; i < 5; i++)
        {
            await BroadcastNodesAsync(broadcaster, Neighbour, info);
        }
        await Wait.ForAsync(() => netRom.Snapshot().NeighbourCount > 0, "the node still hears NODES while in a QSO");

        // The session is unperturbed: still Connected, and a fresh command still works.
        remote.CurrentState.Should().Be("Connected", "a read-only NODES tap must not disturb the live session");
        remote.SendLine("I");
        await Wait.ForAsync(() => remote.Saw("Software: Packet.NET"), "the QSO still carries data after the NODES storm");
    }

    [Fact]
    public async Task A_disabled_service_hears_nothing_and_the_console_shows_no_netrom_section()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var broadcaster = bus.Attach();

        var netRom = new NetRomService(new NetRomConfig { Enabled = false }, TimeProvider.System, NullLogger<NetRomService>.Instance);

        var disabledConfig = Config() with { NetRom = new NetRomConfig { Enabled = false } };
        var config = new TestConfigProvider(disabledConfig);
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        var info = BuildNodesInfo("RDGBPQ", (DestSot, "SOT", ViaXyz, 200));
        for (int i = 0; i < 3; i++)
        {
            await BroadcastNodesAsync(broadcaster, Neighbour, info);
        }

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("NODE"), "banner should arrive on connect");

        remote.SendLine("N");
        await Wait.ForAsync(() => remote.Saw("Ports:"), "Nodes still lists ports when NET/ROM is off");
        netRom.Snapshot().NeighbourCount.Should().Be(0, "a disabled service learns nothing");
        remote.Saw("NET/ROM").Should().BeFalse("no NET/ROM section when disabled");
    }

    // ─── Helpers: build + broadcast a NODES UI frame on the shared bus ───

    private static async Task BroadcastNodesAsync(IKissModem broadcaster, Callsign source, byte[] info)
    {
        // A genuine NODES broadcast: UI frame, source = the broadcasting node,
        // destination = the literal text callsign "NODES", PID 0xCF.
        var frame = Ax25Frame.Ui(
            destination: new Callsign("NODES", 0),
            source: source,
            info: info,
            pid: Ax25Frame.PidNetRom,
            isCommand: true);
        await broadcaster.SendFrameAsync(frame.ToBytes());
    }

    // Mirror the NET/ROM NODES info-field wire format (the production library is
    // read-only, so the test owns the encoder): 0xFF + 6-byte sender alias +
    // 21-byte entries (7-byte shifted dest, 6-byte dest alias, 7-byte shifted
    // best-neighbour, 1-byte quality).
    private static byte[] BuildNodesInfo(string senderAlias, params (Callsign Dest, string Alias, Callsign Via, byte Q)[] entries)
    {
        var buf = new List<byte> { 0xFF };
        buf.AddRange(EncodeAlias(senderAlias));
        foreach (var e in entries)
        {
            buf.AddRange(EncodeShifted(e.Dest));
            buf.AddRange(EncodeAlias(e.Alias));
            buf.AddRange(EncodeShifted(e.Via));
            buf.Add(e.Q);
        }
        return [.. buf];
    }

    private static byte[] EncodeShifted(Callsign call)
    {
        var addr = new Ax25Address(call, CrhBit: false, ExtensionBit: false);
        var bytes = new byte[Ax25Address.EncodedLength];
        addr.Write(bytes);
        return bytes;
    }

    private static byte[] EncodeAlias(string alias)
    {
        var bytes = new byte[6];
        Array.Fill(bytes, (byte)' ');
        for (int i = 0; i < Math.Min(alias.Length, 6); i++)
        {
            bytes[i] = (byte)alias[i];
        }
        return bytes;
    }
}
