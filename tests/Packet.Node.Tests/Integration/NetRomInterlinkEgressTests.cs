using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Which port a NET/ROM interlink actually leaves on. Since PC4
/// (packet-net/packet.net#725) this is not a choice at all: an interlink is keyed by the
/// <see cref="NeighbourKey"/> - the (port, callsign) adjacency - and the caller reached it by
/// following a route, which names its own port. The SABM therefore goes out on the band the
/// route was learned on, every time.
/// </summary>
/// <remarks>
/// <para>
/// The history is worth keeping. It was first <c>attachments.Values.FirstOrDefault()</c> over a
/// <c>ConcurrentDictionary</c>, so a connect-out to a neighbour we had no NODES from picked an
/// arbitrary port: the SABM could go out on the wrong band, and it seeded the per-(port, peer)
/// capability cache for a port the link would not use. PC2 (#723 item 4) made that deterministic
/// as an interim - last-heard port, else the first attached port in configuration order. PC4
/// removes the guess entirely, so the interim rule is gone rather than layered on top.
/// </para>
/// <para>
/// The three ports are named so configuration order and id order disagree - config says
/// <c>vhf</c> first, the alphabet says <c>hf</c> - so a test that passes only because the two
/// coincide is visible.
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class NetRomInterlinkEgressTests
{
    private static readonly Callsign NodeCall = new("M0NODE", 0);
    private static readonly Callsign Neighbour = new("GB7RDG", 0);

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "NODE" },
        NetRom = new NetRomConfig { Enabled = true, Connect = true },
        Ports =
        [
            PortOn("vhf", 1),
            PortOn("hf", 2),
            PortOn("uhf", 3),
        ],
    };

    private static PortConfig PortOn(string id, int port) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new KissTcpTransport { Host = "mem", Port = port },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private sealed record Lab(
        PortSupervisor Supervisor,
        NetRomService NetRom,
        SharedRadioBus Vhf,
        SharedRadioBus Hf,
        SharedRadioBus Uhf);

    private static async Task<Lab> StartAsync()
    {
        var vhf = new SharedRadioBus();
        var hf = new SharedRadioBus();
        var uhf = new SharedRadioBus();
        var cfg = Config();
        var netRom = new NetRomService(cfg.NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: "NODE");
        var config = new TestConfigProvider(cfg);
        var factory = new FakeTransportFactory()
            .Provide("kiss-tcp:mem:1", vhf.Attach())
            .Provide("kiss-tcp:mem:2", hf.Attach())
            .Provide("kiss-tcp:mem:3", uhf.Attach());
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 3, "all three ports up");
        // Canonical order is CONFIG order - note the alphabet would say hf, uhf, vhf.
        supervisor.RunningPortIds.Should().Equal("vhf", "hf", "uhf");
        return new Lab(supervisor, netRom, vhf, hf, uhf);
    }

    [Fact]
    public async Task The_interlink_leaves_on_the_port_named_in_the_key()
    {
        var lab = await StartAsync();
        await using var supervisor = lab.Supervisor;
        using var netRom = lab.NetRom;

        // The neighbour is reachable on every channel, so only the KEY decides which one hears
        // the SABM. 'uhf' is neither first by alphabet ('hf') nor first by config order ('vhf'),
        // so a dial that lands there can only have come from the key.
        await using var onVhf = new EchoStation(lab.Vhf.Attach(), Neighbour, reply: "VHF\r");
        await using var onHf = new EchoStation(lab.Hf.Attach(), Neighbour, reply: "HF\r");
        await using var onUhf = new EchoStation(lab.Uhf.Attach(), Neighbour, reply: "UHF\r");
        await onVhf.StartAsync();
        await onHf.StartAsync();
        await onUhf.StartAsync();

        await netRom.EnsureInterlinkForTestAsync(new NeighbourKey("uhf", Neighbour));

        onUhf.SawConnect.Should().BeTrue("the interlink leaves on the port named in its key - the port of the route being followed");
        onVhf.SawConnect.Should().BeFalse("'vhf' is only first by CONFIG order, which no longer decides an egress port");
        onHf.SawConnect.Should().BeFalse("'hf' is only first by ALPHABET, which never decided anything");
    }

    [Fact]
    public async Task A_key_naming_an_unattached_port_fails_rather_than_dialling_another_band()
    {
        var lab = await StartAsync();
        await using var supervisor = lab.Supervisor;
        using var netRom = lab.NetRom;

        await using var onVhf = new EchoStation(lab.Vhf.Attach(), Neighbour, reply: "VHF\r");
        await using var onHf = new EchoStation(lab.Hf.Attach(), Neighbour, reply: "HF\r");
        await using var onUhf = new EchoStation(lab.Uhf.Attach(), Neighbour, reply: "UHF\r");
        await onVhf.StartAsync();
        await onHf.StartAsync();
        await onUhf.StartAsync();

        Func<Task> dial = async () =>
            await netRom.EnsureInterlinkForTestAsync(new NeighbourKey("satellite", Neighbour));

        await dial.Should().ThrowAsync<InvalidOperationException>(
            "a route over a port that is not attached cannot be followed");
        onVhf.SawConnect.Should().BeFalse("silently dialling another band would put the SABM on a channel the peer may not be on");
        onHf.SawConnect.Should().BeFalse();
        onUhf.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task One_station_audible_on_two_ports_gets_two_interlinks()
    {
        var lab = await StartAsync();
        await using var supervisor = lab.Supervisor;
        using var netRom = lab.NetRom;

        // The neighbour broadcasts NODES on BOTH vhf and hf, so the table holds two adjacencies
        // to one callsign - the dual-homed backbone peer PC4 exists for.
        await BroadcastNodesAsync(lab.Vhf.Attach(), Neighbour, BuildNodesInfo("RDGBPQ"));
        await BroadcastNodesAsync(lab.Hf.Attach(), Neighbour, BuildNodesInfo("RDGBPQ"));
        await Wait.ForAsync(
            () => netRom.Snapshot().Neighbours.Count(n => n.Neighbour == Neighbour) == 2,
            "the node holds one neighbour row per port it heard the station on");

        await using var onVhf = new EchoStation(lab.Vhf.Attach(), Neighbour, reply: "VHF\r");
        await using var onHf = new EchoStation(lab.Hf.Attach(), Neighbour, reply: "HF\r");
        await onVhf.StartAsync();
        await onHf.StartAsync();

        await netRom.EnsureInterlinkForTestAsync(new NeighbourKey("vhf", Neighbour));
        await netRom.EnsureInterlinkForTestAsync(new NeighbourKey("hf", Neighbour));

        onVhf.SawConnect.Should().BeTrue("each adjacency carries its own interlink");
        onHf.SawConnect.Should().BeTrue(
            "the second port is NOT shut out by the first - one interlink per station was the defect (#725)");
    }

    // --- the NODES wire helpers (mirrors NetRomAwareIntegrationTests; the production
    // library's NODES support is read-only, so the test owns the encoder) ---------------

    private static async Task BroadcastNodesAsync(IAx25Transport broadcaster, Callsign source, byte[] info)
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("NODES", 0),
            source: source,
            info: info,
            pid: Ax25Frame.PidNetRom,
            isCommand: true);
        await broadcaster.SendAsync(frame.ToBytes());
    }

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
        for (int i = 0; i < Math.Min(6, alias.Length); i++)
        {
            bytes[i] = (byte)alias[i];
        }
        return bytes;
    }
}
