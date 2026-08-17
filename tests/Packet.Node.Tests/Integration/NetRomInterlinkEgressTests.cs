using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Which port a NET/ROM interlink actually leaves on (packet-net/packet.net#723 item 4). The
/// rule is: the port we last HEARD the neighbour on, else the first attached port in the node's
/// canonical (configuration) order - and it is logged either way, because otherwise the only
/// record of the choice is a frame trace.
/// </summary>
/// <remarks>
/// It used to be <c>attachments.Values.FirstOrDefault()</c> over a <c>ConcurrentDictionary</c>,
/// reached whenever the neighbour had not been heard or its port was not attached. So a
/// connect-out to a neighbour we have no NODES from picked an arbitrary port: the SABM could go
/// out on the wrong band, and it seeded the per-(port, peer) capability cache for a port the link
/// will not normally use. The three ports here are named so configuration order and id order
/// disagree - config says <c>vhf</c> first, the alphabet says <c>hf</c>.
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
    public async Task An_unheard_neighbour_is_dialled_on_the_first_port_in_config_order()
    {
        var lab = await StartAsync();
        await using var supervisor = lab.Supervisor;
        using var netRom = lab.NetRom;

        // The neighbour is reachable on every channel; only the node's CHOICE decides which
        // one hears the SABM. Id order would pick 'hf'; config order picks 'vhf'.
        await using var onVhf = new EchoStation(lab.Vhf.Attach(), Neighbour, reply: "VHF\r");
        await using var onHf = new EchoStation(lab.Hf.Attach(), Neighbour, reply: "HF\r");
        await using var onUhf = new EchoStation(lab.Uhf.Attach(), Neighbour, reply: "UHF\r");
        await onVhf.StartAsync();
        await onHf.StartAsync();
        await onUhf.StartAsync();

        await netRom.EnsureInterlinkForTestAsync(Neighbour);

        onVhf.SawConnect.Should().BeTrue("an unheard neighbour is dialled on the first attached port in config order");
        onHf.SawConnect.Should().BeFalse("'hf' is only first by ALPHABET, which is not the node's ordering");
        onUhf.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task A_heard_neighbour_is_dialled_on_the_port_it_was_heard_on()
    {
        var lab = await StartAsync();
        await using var supervisor = lab.Supervisor;
        using var netRom = lab.NetRom;

        // The neighbour broadcasts NODES on 'hf' only, so that is where we know it lives -
        // last-heard beats the config-order fallback.
        var broadcaster = lab.Hf.Attach();
        await BroadcastNodesAsync(broadcaster, Neighbour, BuildNodesInfo("RDGBPQ"));
        await Wait.ForAsync(
            () => netRom.Snapshot().Neighbours.Any(n => n.Neighbour == Neighbour && n.PortId == "hf"),
            "the node hears the neighbour on hf");

        await using var onVhf = new EchoStation(lab.Vhf.Attach(), Neighbour, reply: "VHF\r");
        await using var onHf = new EchoStation(lab.Hf.Attach(), Neighbour, reply: "HF\r");
        await onVhf.StartAsync();
        await onHf.StartAsync();

        await netRom.EnsureInterlinkForTestAsync(Neighbour);

        onHf.SawConnect.Should().BeTrue("the neighbour's last-heard port wins over the config-order fallback");
        onVhf.SawConnect.Should().BeFalse();
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
