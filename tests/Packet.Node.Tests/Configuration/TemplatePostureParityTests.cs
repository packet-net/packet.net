using Packet.Node.Core.Configuration;
using Xunit;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The node has three annotated first-boot templates: the packaged one
/// (<c>packaging/packetnet.yaml</c>, staged to <c>/usr/share/packetnet/packetnet.yaml.example</c>,
/// which seeds a .deb install's DB), the container one
/// (<c>docker/node/packetnet.container.yaml</c>, baked into the image), and the in-code
/// fallback (<see cref="NodeConfigTemplate"/>, which seeds an archive or dev node where the
/// staged file is absent). A fresh node's security/reachability posture must not depend on
/// which of the three it happened to seed from: the 2026-08-03 defaults flip updated the
/// packaged template and missed the in-code one, which left an archive install loopback-bound
/// and unreachable from the LAN.
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

    /// <summary>
    /// C069: every template seeds a FIRST-BOOT config, so each must stamp the version this
    /// build actually produces. They said 1 while <see cref="NodeConfig.CurrentSchemaVersion"/>
    /// was 2, so every fresh install persisted a stale row and re-ran the migration chain on
    /// every boot. Bumping CurrentSchemaVersion without bumping the templates now fails here.
    /// </summary>
    [Fact]
    public void All_templates_stamp_the_schema_version_this_build_produces()
    {
        var inCode = NodeConfigYaml.Parse(NodeConfigTemplate.Yaml);
        var packaged = NodeConfigYaml.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "packetnet.yaml")));

        inCode.SchemaVersion.Should().Be(NodeConfig.CurrentSchemaVersion);
        packaged.SchemaVersion.Should().Be(NodeConfig.CurrentSchemaVersion);
        Container().SchemaVersion.Should().Be(NodeConfig.CurrentSchemaVersion);
    }

    /// <summary>
    /// The container template is a first-boot seed like the other two, and it differs from the
    /// packaged one only in what a container needs (telnet off, no commented examples). Its
    /// security posture - LAN bind plus a login - must not diverge; it is the template most
    /// likely to be published to a hostile network.
    /// </summary>
    [Fact]
    public void Container_template_keeps_the_packaged_security_posture()
    {
        var packaged = NodeConfigYaml.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "packetnet.yaml")));
        var container = Container();

        container.Management.Http.Bind.Should().Be(packaged.Management.Http.Bind);
        container.Management.Http.Port.Should().Be(packaged.Management.Http.Port);
        container.Management.Auth.Enabled.Should().Be(packaged.Management.Auth.Enabled,
            "the panel is published wherever `docker run -p` puts it");
    }

    private static NodeConfig Container() => NodeConfigYaml.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "docker", "node", "packetnet.container.yaml")));

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
