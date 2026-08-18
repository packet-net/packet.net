using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Pins the value-equality contract on the config records that carry a collection
/// member. A C# record compares a collection member by <b>reference</b>, so two
/// configs with equal-but-distinct lists/dicts (exactly what a YAML serialise→parse
/// round-trip yields) would be unequal - breaking change-detection identity. Each
/// such record hand-rolls <c>Equals</c>/<c>GetHashCode</c> over its collection via
/// <c>ConfigEquality</c>; these tests assert (a) equal-but-distinct collections
/// compare equal, (b) differing collection content compares unequal, and (c) every
/// scalar field still participates in equality (a guard against a member dropped
/// from the hand-rolled <c>Equals</c>).
/// </summary>
public class ConfigCollectionEqualityTests
{
    private static ApplicationConfig SampleApp() => new()
    {
        Id = "myapp",
        Command = "MYAPP",
        Enabled = true,
        Kind = ApplicationKind.Process,
        Executable = "/usr/bin/python3",
        SocketPath = null,
        Args = ["script.py", "--flag"],
        WorkingDirectory = "/srv/app",
        Capabilities = ["session", "network"],
        Ui = new AppUiConfig { Upstream = "http://127.0.0.1:9090", Name = "App", Mode = AppUiMode.Embedded },
        Callsign = "M0ABC-1",
        Netrom = new AppNetromConfig { Alias = "APP", Quality = 200 },
    };

    private static AppOverrideConfig SampleOverride() => new()
    {
        Id = "pkg",
        Enabled = true,
        Command = "PKG",
        Callsign = "M0ABC-2",
        Netrom = new AppNetromConfig { Alias = "PKG", Quality = 180 },
        Environment = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
    };

    [Fact]
    public void ApplicationConfig_with_equal_but_distinct_collections_is_equal()
    {
        var a = SampleApp();
        // Fresh list instances, identical content - the round-trip scenario.
        var b = a with { Args = ["script.py", "--flag"], Capabilities = ["session", "network"] };

        b.Should().Be(a);
        b.GetHashCode().Should().Be(a.GetHashCode());
    }

    [Fact]
    public void ApplicationConfig_differing_list_content_is_unequal()
    {
        var a = SampleApp();

        (a with { Args = ["script.py"] }).Should().NotBe(a);                 // dropped element
        (a with { Args = ["script.py", "--other"] }).Should().NotBe(a);      // changed element
        (a with { Capabilities = ["session"] }).Should().NotBe(a);
    }

    [Fact]
    public void ApplicationConfig_every_field_participates_in_equality()
    {
        var a = SampleApp();

        (a with { Id = "other" }).Should().NotBe(a);
        (a with { Command = "OTHER" }).Should().NotBe(a);
        (a with { Enabled = false }).Should().NotBe(a);
        (a with { Kind = ApplicationKind.Socket }).Should().NotBe(a);
        (a with { Executable = "/bin/sh" }).Should().NotBe(a);
        (a with { SocketPath = "/run/x.sock" }).Should().NotBe(a);
        (a with { WorkingDirectory = "/tmp" }).Should().NotBe(a);
        (a with { Callsign = "M0XYZ-3" }).Should().NotBe(a);
        (a with { Ui = a.Ui! with { Name = "Different" } }).Should().NotBe(a);
        (a with { Netrom = new AppNetromConfig { Alias = "ZZZ" } }).Should().NotBe(a);
    }

    [Fact]
    public void AppOverrideConfig_with_equal_but_distinct_environment_is_equal()
    {
        var a = SampleOverride();
        // Fresh dictionary, identical content but different insertion order.
        var b = a with { Environment = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" } };

        b.Should().Be(a);
        b.GetHashCode().Should().Be(a.GetHashCode());
    }

    [Fact]
    public void AppOverrideConfig_differing_environment_is_unequal()
    {
        var a = SampleOverride();

        (a with { Environment = new Dictionary<string, string> { ["A"] = "1" } }).Should().NotBe(a);            // missing key
        (a with { Environment = new Dictionary<string, string> { ["A"] = "9", ["B"] = "2" } }).Should().NotBe(a); // changed value
    }

    [Fact]
    public void AppOverrideConfig_every_field_participates_in_equality()
    {
        var a = SampleOverride();

        (a with { Id = "other" }).Should().NotBe(a);
        (a with { Enabled = false }).Should().NotBe(a);
        (a with { Command = "OTHER" }).Should().NotBe(a);
        (a with { Callsign = "M0XYZ-9" }).Should().NotBe(a);
        (a with { Netrom = new AppNetromConfig { Alias = "ZZZ" } }).Should().NotBe(a);
    }

    [Fact]
    public void WebAuthnConfig_with_equal_but_distinct_origins_is_equal()
    {
        var a = new WebAuthnConfig
        {
            RelyingPartyId = "pdn.example",
            RelyingPartyName = "pdn",
            AllowedOrigins = ["https://pdn.example", "https://pdn.example:8443"],
        };
        var b = a with { AllowedOrigins = ["https://pdn.example", "https://pdn.example:8443"] };

        b.Should().Be(a);
        b.GetHashCode().Should().Be(a.GetHashCode());
        (a with { AllowedOrigins = ["https://pdn.example"] }).Should().NotBe(a);
    }

    [Fact]
    public void TailscaleConfig_with_equal_but_distinct_tags_is_equal()
    {
        var a = new TailscaleConfig { Enabled = true, Tags = ["tag:server", "tag:packetnet"] };
        var b = a with { Tags = ["tag:server", "tag:packetnet"] };

        b.Should().Be(a);
        b.GetHashCode().Should().Be(a.GetHashCode());
        (a with { Tags = ["tag:server"] }).Should().NotBe(a);
    }
}
