using Packet.Node.Core.Configuration;
using Xunit;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The node has two annotated first-boot templates: the packaged one
/// (<c>packaging/packetnet.yaml</c>, staged to <c>/usr/share/packetnet/packetnet.yaml.example</c>,
/// which seeds a .deb install's DB) and the in-code fallback (<see cref="NodeConfigTemplate"/>,
/// which seeds an archive or dev node where the staged file is absent). A fresh node's
/// security/reachability posture must not depend on which of the two it happened to seed
/// from: the 2026-08-03 defaults flip updated the packaged template and missed the in-code
/// one, which left an archive install loopback-bound and unreachable from the LAN.
/// </summary>
public sealed class TemplatePostureParityTests
{
    [Fact]
    public void In_code_and_packaged_templates_agree_on_first_contact_posture()
    {
        var inCode = NodeConfigYaml.Parse(NodeConfigTemplate.Yaml);
        var packaged = NodeConfigYaml.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "packetnet.yaml")));

        inCode.Management.Http.Bind.Should().Be(packaged.Management.Http.Bind,
            "a fresh node must meet the world the same way whichever template seeded it");
        inCode.Management.Http.Port.Should().Be(packaged.Management.Http.Port);
        inCode.Management.Auth.Enabled.Should().Be(packaged.Management.Auth.Enabled,
            "the LAN bind and the login requirement were flipped together and must stay together");
        inCode.Management.Telnet.Bind.Should().Be(packaged.Management.Telnet.Bind);
        inCode.Management.Telnet.Port.Should().Be(packaged.Management.Telnet.Port);
    }

    /// <summary>Walk up from the test assembly to the repo root (the directory that has
    /// <c>packaging/packetnet.yaml</c>), same approach as <c>ShippedManifestsTests</c>.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "packaging", "packetnet.yaml")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no packaging/packetnet.yaml above the test assembly).");
    }
}
