using Packet.Node.Core.Configuration;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// The health URL the node spools for the privileged github-update helper
/// (<see cref="GithubUpdateRequestBuilder.HealthUrlFor"/>). The helper used to hard-code
/// <c>http://127.0.0.1:8080/healthz</c>, so a node serving any other port failed its own health
/// gate after a good install and was rolled back (packet.net#699 / C101). It must follow the
/// configured port, and it must stay a loopback URL - that is the only shape the helper accepts,
/// because the spool it reads is writable by the unprivileged service user.
/// </summary>
[Trait("Category", "Node")]
public sealed class GithubHealthUrlTests
{
    [Theory]
    // The stock posture: wildcard bind, default port - reachable on loopback.
    [InlineData("0.0.0.0", 8080, "http://127.0.0.1:8080/healthz")]
    // The bug: a non-8080 port must move the gate with it.
    [InlineData("0.0.0.0", 9090, "http://127.0.0.1:9090/healthz")]
    [InlineData("127.0.0.1", 8011, "http://127.0.0.1:8011/healthz")]
    // A specific LAN bind still normalises to loopback (the helper refuses anything else); the
    // probe then fails and the helper's `systemctl is-active` fallback carries the gate.
    [InlineData("192.168.1.50", 8080, "http://127.0.0.1:8080/healthz")]
    // IPv6 binds get the bracketed v6 loopback.
    [InlineData("::", 8080, "http://[::1]:8080/healthz")]
    [InlineData("::1", 8080, "http://[::1]:8080/healthz")]
    // Unparseable bind: Kestrel falls back to IPv4 loopback in Program.cs, so we do too.
    [InlineData("not-an-address", 8080, "http://127.0.0.1:8080/healthz")]
    public void Health_url_follows_the_configured_bind_and_port(string bind, int port, string expected)
    {
        GithubUpdateRequestBuilder.HealthUrlFor(new HttpConfig { Bind = bind, Port = port })
            .Should().Be(expected);
    }

    [Fact]
    public void Health_url_for_the_default_config_is_the_loopback_default()
    {
        // A stock node's URL must be exactly the helper's own fallback, so the two can never
        // disagree on the default.
        GithubUpdateRequestBuilder.HealthUrlFor(new HttpConfig())
            .Should().Be("http://127.0.0.1:8080/healthz");
    }
}
