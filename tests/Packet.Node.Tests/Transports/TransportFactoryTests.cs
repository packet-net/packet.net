using Packet.Kiss;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The real <see cref="TransportFactory"/>'s AXUDP arm: an <see cref="AxudpTransport"/>
/// is mapped onto a live <see cref="AxudpKissModem"/> over a UDP socket. AXUDP is still
/// an <c>IKissModem</c> (its frame synthesis migrates in a later step), so the factory
/// adapts it to the neutral <c>IAx25Transport</c> seam through the migration shim
/// (<see cref="KissModemTransport"/>) — the factory uniformly returns
/// <c>IAx25Transport</c>. (The serial / kiss-tcp arms open real hardware/sockets and
/// are covered through the integration harness with a fake factory; this pins the
/// AXUDP wiring.)
/// </summary>
public sealed class TransportFactoryTests
{
    [Fact]
    public async Task Creates_an_AxudpKissModem_for_an_axudp_transport_binding_the_local_port()
    {
        var transport = new AxudpTransport { Host = "127.0.0.1", Port = 10093, LocalPort = 0 };

        var created = await TransportFactory.Instance.CreateAsync(transport);
        await using (created)
        {
            var shim = created.Should().BeOfType<KissModemTransport>().Subject;
            var axudp = shim.Modem.Should().BeOfType<AxudpKissModem>().Subject;
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
            var shim = created.Should().BeOfType<KissModemTransport>().Subject;
            shim.Modem.Should().BeOfType<AxudpKissModem>();
        }
    }
}
