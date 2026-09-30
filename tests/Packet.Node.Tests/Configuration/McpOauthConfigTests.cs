using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The two #427 knobs on <c>mcp.oauth</c>: the pinned issuer's shape, and the startup
/// judgement of whether the consent page is reachable over cleartext from beyond the host.
/// </summary>
public sealed class McpOauthConfigTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig WithOauth(string? issuer, bool enabled = true) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [],
        Management = new ManagementConfig
        {
            Auth = new AuthConfig { Enabled = true },
        },
        Mcp = new McpConfig { Oauth = new McpOauthConfig { Enabled = enabled, Issuer = issuer } },
    };

    [Theory]
    [InlineData(null)]
    [InlineData("https://pdn.example:8443")]
    [InlineData("https://pdn.example/")]
    [InlineData("https://pdn.example/oauth")]
    [InlineData("http://127.0.0.1:8080")]
    public void An_absolute_http_url_or_none_is_an_acceptable_issuer(string? issuer)
    {
        Validator.Validate(WithOauth(issuer)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("pdn.example")]
    [InlineData("/oauth")]
    [InlineData("ftp://pdn.example")]
    [InlineData("https://pdn.example/?x=1")]
    [InlineData("https://pdn.example/#frag")]
    [InlineData("")]
    public void Anything_else_is_refused_as_an_issuer(string issuer)
    {
        var result = Validator.Validate(WithOauth(issuer));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("mcp.oauth.issuer", StringComparison.Ordinal));
    }

    private static NodeConfig Bound(string httpBind, bool https = false, bool tailscale = false, bool oauth = true) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [],
        Management = new ManagementConfig
        {
            Auth = new AuthConfig { Enabled = true },
            Http = new HttpConfig { Bind = httpBind, Port = 8080 },
            Https = new HttpsConfig { Enabled = https },
        },
        Tailscale = new TailscaleConfig { Enabled = tailscale },
        Mcp = new McpConfig { Oauth = new McpOauthConfig { Enabled = oauth } },
    };

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    public void Oauth_on_a_plain_http_bind_beyond_loopback_is_exposed(string bind)
    {
        McpOauthCleartext.Exposed(Bound(bind)).Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void A_loopback_bind_is_not(string bind)
    {
        McpOauthCleartext.Exposed(Bound(bind)).Should().BeFalse();
    }

    [Fact]
    public void Tls_on_the_listener_or_the_tailscale_sidecar_settles_it_and_so_does_oauth_being_off()
    {
        McpOauthCleartext.Exposed(Bound("0.0.0.0", https: true)).Should().BeFalse();
        McpOauthCleartext.Exposed(Bound("0.0.0.0", tailscale: true)).Should().BeFalse();
        McpOauthCleartext.Exposed(Bound("0.0.0.0", oauth: false)).Should().BeFalse();
    }
}
