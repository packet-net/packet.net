using System.Net;
using Packet.Node.Core.Hosting;
using Xunit;

namespace Packet.Node.Tests.Hosting;

public sealed class PanelUrlsTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Specific_bind_yields_that_address_only()
    {
        var urls = PanelUrls.For("192.168.1.10", 8080, [Ip("10.0.0.5")]);
        urls.Should().Equal("http://192.168.1.10:8080");
    }

    [Fact]
    public void Loopback_bind_yields_loopback()
    {
        var urls = PanelUrls.For("127.0.0.1", 8080, [Ip("192.168.1.10")]);
        urls.Should().Equal("http://127.0.0.1:8080");
    }

    [Fact]
    public void Wildcard_expands_to_machine_v4_addresses()
    {
        var urls = PanelUrls.For("0.0.0.0", 8080, [Ip("192.168.1.10"), Ip("10.45.0.3")]);
        urls.Should().Equal("http://192.168.1.10:8080", "http://10.45.0.3:8080");
    }

    [Fact]
    public void Wildcard_skips_loopback_and_link_local()
    {
        var urls = PanelUrls.For("0.0.0.0", 8080,
            [Ip("127.0.0.1"), Ip("169.254.12.34"), Ip("192.168.1.10"), Ip("fe80::1")]);
        urls.Should().Equal("http://192.168.1.10:8080");
    }

    [Fact]
    public void Wildcard_with_no_usable_v4_falls_back_to_global_v6()
    {
        var urls = PanelUrls.For("::", 8080, [Ip("fe80::1"), Ip("2001:db8::7")]);
        urls.Should().Equal("http://[2001:db8::7]:8080");
    }

    [Fact]
    public void Wildcard_with_no_usable_addresses_falls_back_to_loopback()
    {
        var urls = PanelUrls.For("0.0.0.0", 8080, []);
        urls.Should().Equal("http://127.0.0.1:8080");
    }

    [Fact]
    public void Ipv6_any_expands_like_v4_wildcard_because_dual_mode_accepts_v4()
    {
        var urls = PanelUrls.For("::", 8080, [Ip("192.168.1.10")]);
        urls.Should().Equal("http://192.168.1.10:8080");
    }

    [Fact]
    public void Unparseable_bind_mirrors_kestrels_loopback_fallback()
    {
        var urls = PanelUrls.For("not-an-ip", 8080, [Ip("192.168.1.10")]);
        urls.Should().Equal("http://127.0.0.1:8080");
    }

    [Fact]
    public void Duplicate_addresses_collapse()
    {
        var urls = PanelUrls.For("0.0.0.0", 9090, [Ip("192.168.1.10"), Ip("192.168.1.10")]);
        urls.Should().Equal("http://192.168.1.10:9090");
    }

    [Fact]
    public void Specific_v6_bind_is_bracketed()
    {
        var urls = PanelUrls.For("2001:db8::7", 8080, []);
        urls.Should().Equal("http://[2001:db8::7]:8080");
    }
}
