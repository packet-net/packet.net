using FsCheck.Xunit;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The codec round-trip property: serialising any valid <see cref="NodeConfig"/>
/// and parsing it back yields an equivalent config. Exercising every transport
/// variant is what catches discriminator bugs (a <c>kind:</c> that serialises one
/// way and parses another).
/// </summary>
public class NodeConfigRoundTripProperties
{
    [Property(Arbitrary = [typeof(NodeConfigArbitraries)], MaxTest = 300)]
    public void Serialise_then_parse_is_identity(NodeConfig config)
    {
        var yaml = NodeConfigYaml.Serialize(config);
        var reparsed = NodeConfigYaml.Parse(yaml);

        // Records give structural equality; the IReadOnlyList<PortConfig> compares
        // by reference under record equality, so compare the pieces explicitly.
        reparsed.Identity.Should().Be(config.Identity, "round-trip should preserve identity\nYAML:\n{0}", yaml);
        reparsed.Ports.Should().Equal(config.Ports, "round-trip should preserve every port (incl. its transport kind)\nYAML:\n{0}", yaml);
        reparsed.Applications.Should().Equal(config.Applications, "round-trip should preserve registered applications (incl. Args/Capabilities)\nYAML:\n{0}", yaml);
        reparsed.Apps.Should().Equal(config.Apps, "round-trip should preserve app-package overrides (incl. Environment)\nYAML:\n{0}", yaml);
        reparsed.Services.Should().Be(config.Services);
        reparsed.Management.Should().Be(config.Management);
        reparsed.Traffic.Should().Be(config.Traffic, "round-trip should preserve the traffic-log block\nYAML:\n{0}", yaml);
        reparsed.Tailscale.Should().Be(config.Tailscale, "round-trip should preserve the tailscale block (incl. its tags list)\nYAML:\n{0}", yaml);
    }

    [Property(Arbitrary = [typeof(NodeConfigArbitraries)], MaxTest = 300)]
    public void Every_generated_config_is_valid(NodeConfig config)
    {
        // Sanity on the generator: the configs we round-trip really are valid (so
        // the round-trip property tests the codec, not validation leakage).
        var result = new NodeConfigValidator().Validate(config);
        result.IsValid.Should().BeTrue("generated config should be valid: {0}",
            string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }
}
