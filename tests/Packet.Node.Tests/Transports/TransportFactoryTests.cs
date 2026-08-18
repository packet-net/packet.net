using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The real <see cref="TransportFactory"/>'s AXUDP arm: an <see cref="AxudpTransport"/> config
/// is mapped onto a live, NATIVE <see cref="AxudpFrameTransport"/> over a UDP socket - returned
/// directly as <c>IAx25Transport</c>, no KISS shim (AXUDP is not KISS). (The serial / kiss-tcp
/// arms open real hardware/sockets and are covered through the integration harness with a fake
/// factory; this pins the AXUDP wiring.)
/// </summary>
public sealed class TransportFactoryTests
{
    [Fact]
    public async Task Creates_an_AxudpFrameTransport_for_an_axudp_transport_binding_the_local_port()
    {
        var transport = new AxudpTransport { Host = "127.0.0.1", Port = 10093, LocalPort = 0 };

        var created = await TransportFactory.Instance.CreateAsync(transport);
        await using (created)
        {
            var axudp = created.Should().BeOfType<AxudpFrameTransport>().Subject;
            axudp.LocalPort.Should().BeGreaterThan(0, "localPort 0 resolves to a real ephemeral bind");
        }
    }

    [Fact]
    public async Task Resolves_a_hostname_to_an_endpoint()
    {
        // localhost always resolves; this exercises the DNS path (not a literal IP).
        var transport = new AxudpTransport { Host = "localhost", Port = 10093, LocalPort = 0 };

        var created = await TransportFactory.Instance.CreateAsync(transport);
        await using (created)
        {
            created.Should().BeOfType<AxudpFrameTransport>();
        }
    }

    [Fact]
    public async Task Creates_an_AxudpMultipointFrameTransport_resolving_each_peer_and_binding_the_local_port()
    {
        // The multipoint arm: each peer host (literal + a name) is resolved to an endpoint and
        // the one shared socket binds the configured local port (0 → ephemeral here).
        var transport = new AxudpMultipointTransport
        {
            LocalPort = 0,
            Peers =
            [
                new AxudpPeerConfig { Call = "GB7OUK", Host = "127.0.0.1", Port = 10094, Broadcast = true },
                new AxudpPeerConfig { Call = "M0LTE-9", Host = "localhost", Port = 10093, Broadcast = false },
            ],
        };

        var created = await TransportFactory.Instance.CreateAsync(transport);
        await using (created)
        {
            var mp = created.Should().BeOfType<AxudpMultipointFrameTransport>().Subject;
            mp.LocalPort.Should().BeGreaterThan(0, "localPort 0 resolves to a real ephemeral bind");
        }
    }
}
