using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Configuration;

public class FileConfigProviderTests : IDisposable
{
    private readonly string dir;
    private readonly string path;

    public FileConfigProviderTests()
    {
        dir = TestPaths.NewPath("packetnet-cfg");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "node.yaml");
    }

    private const string ValidYaml = """
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
        ports: []
        """;

    private FileConfigProvider Open(string yaml)
    {
        File.WriteAllText(path, yaml);
        return new FileConfigProvider(path, new FakeTimeProvider(), watch: false);
    }

    [Fact]
    public void First_start_writes_a_commented_template_and_starts_idle()
    {
        // File absent → template written, node boots on the placeholder.
        using var provider = new FileConfigProvider(path, new FakeTimeProvider(), watch: false);

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("# Packet.NET node configuration.");
        provider.Current.Identity.Callsign.Should().Be(NodeConfigTemplate.PlaceholderCallsign);
        provider.Current.Ports.Should().BeEmpty();
    }

    [Fact]
    public void Initial_invalid_config_throws_on_construction()
    {
        File.WriteAllText(path, "identity:\n  callsign: not a callsign!\n");
        var act = () => new FileConfigProvider(path, new FakeTimeProvider(), watch: false);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reload_applies_a_valid_change_and_raises_OnChange()
    {
        using var provider = Open(ValidYaml);
        NodeConfig? observed = null;
        using var _ = provider.OnChange(c => observed = c);

        File.WriteAllText(path, """
            schemaVersion: 1
            identity:
              callsign: G7XYZ-7
            ports: []
            """);
        var applied = provider.Reload();

        applied.Should().BeTrue();
        provider.Current.Identity.Callsign.Should().Be("G7XYZ-7");
        observed.Should().NotBeNull();
        observed!.Identity.Callsign.Should().Be("G7XYZ-7");
    }

    [Fact]
    public void Invalid_candidate_is_rejected_atomically_Current_unchanged_no_OnChange()
    {
        using var provider = Open(ValidYaml);
        var before = provider.Current;
        int onChangeCount = 0;
        using var _ = provider.OnChange(_ => onChangeCount++);

        // Write a config that parses but fails validation (bad callsign).
        File.WriteAllText(path, "identity:\n  callsign: 'not valid!'\nports: []\n");
        var applied = provider.Reload();

        applied.Should().BeFalse();
        provider.Current.Should().BeSameAs(before);   // rollback by construction
        onChangeCount.Should().Be(0);
    }

    [Fact]
    public void Malformed_yaml_candidate_is_rejected_Current_unchanged()
    {
        using var provider = Open(ValidYaml);
        var before = provider.Current;

        // Unknown transport kind → parse throws → candidate rejected.
        File.WriteAllText(path, """
            schemaVersion: 1
            identity:
              callsign: M0LTE
            ports:
              - id: x
                transport:
                  kind: smoke-signals
                  host: 1.2.3.4
                  port: 9
            """);
        provider.Reload().Should().BeFalse();
        provider.Current.Should().BeSameAs(before);
    }

    [Fact]
    public void Surfaces_the_netrom_routing_backcompat_warning_at_load()
    {
        // The inert legacy combo (forward:true without connect) parses + validates but is a
        // no-op the operator should know about — it must be surfaced on the boot log.
        File.WriteAllText(path, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              forward: true
            """);
        var captured = new CapturingLogger<FileConfigProvider>();
        using var provider = new FileConfigProvider(path, new FakeTimeProvider(), captured, watch: false);

        provider.Current.NetRom.EffectiveRouting.Should().Be(NetRomRouting.None);
        captured.Warnings.Should().ContainSingle(w => w.Contains("forward") && w.Contains("none"));
    }

    [Fact]
    public void No_warning_for_a_clean_routing_config_at_load()
    {
        File.WriteAllText(path, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              routing: transit
            """);
        var captured = new CapturingLogger<FileConfigProvider>();
        using var provider = new FileConfigProvider(path, new FakeTimeProvider(), captured, watch: false);

        provider.Current.NetRom.EffectiveRouting.Should().Be(NetRomRouting.Transit);
        captured.Warnings.Should().NotContain(w => w.Contains("forward") || w.Contains("routing"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
