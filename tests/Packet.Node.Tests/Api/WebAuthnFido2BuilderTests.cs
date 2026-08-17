using Microsoft.AspNetCore.Http;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Api;

/// <summary>
/// Locks <see cref="WebAuthnFido2Builder"/>'s accepted-origin computation — the
/// load-bearing tier split between the zero-config localhost default (tier 1) and the
/// operator-pinned real-domain path (tier 3, e.g. <c>pdn.m0lte.uk</c> reached from a
/// phone on the LAN). The security-critical property: when origins are pinned, the
/// request's own (potentially spoofed) origin must NOT widen the trusted set.
/// </summary>
[Trait("Category", "Node")]
public sealed class WebAuthnFido2BuilderTests
{
    private static HttpRequest RequestFor(string scheme, string host, int? port = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = port is { } p ? new HostString(host, p) : new HostString(host);
        return ctx.Request;
    }

    [Fact]
    public void ServingOrigin_renders_scheme_host_and_nondefault_port()
    {
        var req = RequestFor("https", "pdn.m0lte.uk", 8443);
        WebAuthnFido2Builder.ServingOrigin(req).Should().Be("https://pdn.m0lte.uk:8443");
    }

    [Fact]
    public void ServingOrigin_has_no_port_when_the_host_header_carries_none()
    {
        // A browser hitting the default port (https→443) sends a bare `Host: pdn.m0lte.uk`
        // with no port, and the origin it then signs into clientDataJSON likewise has no
        // port — so the serving origin we derive must match (no port).
        var req = RequestFor("https", "pdn.m0lte.uk");
        WebAuthnFido2Builder.ServingOrigin(req).Should().Be("https://pdn.m0lte.uk");
    }

    [Fact]
    public void ZeroConfig_default_trusts_the_serving_origin_plus_loopback()
    {
        // The localhost-first default: empty AllowedOrigins ⇒ accept the origin the
        // browser actually used, plus the loopback secure-context origins.
        var cfg = new WebAuthnConfig();   // RelyingPartyId=localhost, AllowedOrigins=[]
        var req = RequestFor("http", "localhost", 8080);

        var origins = WebAuthnFido2Builder.AcceptedOrigins(cfg, req);

        origins.Should().Contain("http://localhost:8080");
        origins.Should().Contain("http://localhost");
    }

    [Fact]
    public void PinnedOrigins_trust_only_the_configured_set()
    {
        // The tier-3 real-domain path: the operator pins the exact origin the phone uses.
        var cfg = new WebAuthnConfig
        {
            RelyingPartyId = "pdn.m0lte.uk",
            AllowedOrigins = ["https://pdn.m0lte.uk:8443"],
        };
        // A request that arrives at the SAME origin is accepted.
        var req = RequestFor("https", "pdn.m0lte.uk", 8443);

        var origins = WebAuthnFido2Builder.AcceptedOrigins(cfg, req);

        origins.Should().Equal(["https://pdn.m0lte.uk:8443"]);
    }

    [Fact]
    public void PinnedOrigins_do_not_admit_a_spoofed_host()
    {
        // The security property: with origins pinned, a request arriving with a different
        // (spoofed / unexpected) Host must NOT be able to add itself to the trusted set.
        // Only the operator-configured origins are ever trusted.
        var cfg = new WebAuthnConfig
        {
            RelyingPartyId = "pdn.m0lte.uk",
            AllowedOrigins = ["https://pdn.m0lte.uk:8443"],
        };
        var spoofed = RequestFor("https", "evil.example", 8443);

        var origins = WebAuthnFido2Builder.AcceptedOrigins(cfg, spoofed);

        origins.Should().Equal(["https://pdn.m0lte.uk:8443"]);
        origins.Should().NotContain("https://evil.example:8443");
    }

    [Fact]
    public void PinnedOrigins_trim_a_trailing_slash()
    {
        // A configured origin with a trailing slash is normalised to the bare origin
        // (the WebAuthn clientDataJSON origin never has one).
        var cfg = new WebAuthnConfig { AllowedOrigins = ["https://pdn.m0lte.uk:8443/"] };
        var req = RequestFor("https", "pdn.m0lte.uk", 8443);

        var origins = WebAuthnFido2Builder.AcceptedOrigins(cfg, req);

        origins.Should().Equal(["https://pdn.m0lte.uk:8443"]);
    }

    [Fact]
    public void PinnedOrigins_can_list_more_than_one()
    {
        // Reachable under more than one name (e.g. a split-horizon LAN name + a public
        // name) — both pinned, both trusted, nothing else.
        var cfg = new WebAuthnConfig
        {
            RelyingPartyId = "pdn.m0lte.uk",
            AllowedOrigins = ["https://pdn.m0lte.uk:8443", "https://pdn.m0lte.uk"],
        };
        var req = RequestFor("https", "pdn.m0lte.uk", 8443);

        var origins = WebAuthnFido2Builder.AcceptedOrigins(cfg, req);

        origins.Count.Should().Be(2);
        origins.Should().Contain("https://pdn.m0lte.uk:8443");
        origins.Should().Contain("https://pdn.m0lte.uk");
    }
}
