using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The Stage-3b wiring of <see cref="HeadEndDeviceResolver"/> to the address source: a head-end
/// configured in discover mode (blank address) resolves its device binding through an mDNS browse of
/// its instance id — the "plug in and go" bring-up path — while keeping <c>(instanceId, deviceId)</c>
/// keying so a re-addressed head-end still resolves.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndDeviceResolverMdnsTests
{
    [Fact]
    public async Task Resolves_a_blank_address_head_end_device_via_mdns()
    {
        using var pipe = new LoopbackRawPipe();
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "nino0", TcpPort = pipe.Port, Baud = 57600 }],
        });
        var headEnds = new[] { new HeadEndConfig { Id = "pi-shack", Address = "" } };
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));
        var addressResolver = new HeadEndAddressResolver(headEnds, discovery);

        var resolver = new HeadEndDeviceResolver(
            headEnds,
            clientFactory: uri => new HeadEndClient(uri, new HttpClient(handler)),
            addressResolver: addressResolver);

        var binding = await resolver.ResolveAsync("pi-shack", "nino0");

        binding.Host.Should().Be("127.0.0.1", "the dial host came from the mDNS-discovered address");
        binding.TcpPort.Should().Be(pipe.Port);
        binding.Baud.Should().Be(57600);
        discovery.Browses.Should().Be(1);
    }

    [Fact]
    public async Task A_blank_address_that_does_not_resolve_throws_a_clear_open_failure()
    {
        var headEnds = new[] { new HeadEndConfig { Id = "pi-shack", Address = "" } };
        var discovery = new FakeHeadEndDiscovery(); // nothing discovered
        var resolver = new HeadEndDeviceResolver(
            headEnds, addressResolver: new HeadEndAddressResolver(headEnds, discovery));

        var act = () => resolver.ResolveAsync("pi-shack", "nino0");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no resolvable address*");
    }
}
