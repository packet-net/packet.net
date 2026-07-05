using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The Stage-3b address source (<see cref="HeadEndAddressResolver"/>): a pinned config address wins
/// (and is never second-guessed by a browse); a blank address falls back to a single unambiguous
/// mDNS hit; a duplicate-instance-id discovery is refused (null), never guessed.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndAddressResolverTests
{
    [Fact]
    public async Task Prefers_a_config_pinned_address_over_mdns_and_never_browses()
    {
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi", "192.168.1.9", 7300));
        var resolver = new HeadEndAddressResolver(
            [new HeadEndConfig { Id = "pi", Address = "10.0.0.5:7300" }], discovery);

        var uri = await resolver.ResolveBaseAddressAsync("pi");

        uri.Should().Be(new Uri("http://10.0.0.5:7300/"));
        discovery.Browses.Should().Be(0, "a pinned address is authoritative — no browse, no override");
    }

    [Fact]
    public async Task Falls_back_to_a_single_mdns_hit_when_the_config_address_is_blank()
    {
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi", "192.168.1.9", 7300));
        var resolver = new HeadEndAddressResolver(
            [new HeadEndConfig { Id = "pi", Address = "" }], discovery);

        var uri = await resolver.ResolveBaseAddressAsync("pi");

        uri.Should().Be(new Uri("http://192.168.1.9:7300/"));
        discovery.Browses.Should().Be(1);
    }

    [Fact]
    public async Task Resolves_a_discovered_head_end_that_is_not_in_config()
    {
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi", "192.168.1.9", 7300));
        var resolver = new HeadEndAddressResolver([], discovery);

        (await resolver.ResolveBaseAddressAsync("pi")).Should().Be(new Uri("http://192.168.1.9:7300/"));
    }

    [Fact]
    public async Task A_duplicate_instance_id_with_no_config_address_resolves_to_null()
    {
        var discovery = new FakeHeadEndDiscovery(
            new DiscoveredHeadEnd("pi", "192.168.1.9", 7300),
            new DiscoveredHeadEnd("pi", "192.168.1.42", 7300));
        var resolver = new HeadEndAddressResolver([], discovery);

        (await resolver.ResolveBaseAddressAsync("pi")).Should().BeNull("a spoofed/duplicate id must not silently bind");
    }

    [Fact]
    public async Task An_unknown_head_end_resolves_to_null()
    {
        var resolver = new HeadEndAddressResolver([], new FakeHeadEndDiscovery());

        (await resolver.ResolveBaseAddressAsync("ghost")).Should().BeNull();
    }
}
