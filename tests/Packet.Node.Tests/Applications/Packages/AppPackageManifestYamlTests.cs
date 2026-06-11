using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

public class AppPackageManifestYamlTests
{
    [Fact]
    public void Parses_a_full_manifest_including_kebab_enums_and_the_environment_map()
    {
        const string yaml = """
            manifest: 1
            id: lobby
            name: LOBBY
            version: "1.0.0"
            description: Live multi-user lobby — WHO presence + SAY broadcast.
            icon: users
            capabilities: [session, network]
            session:
              match: LOBBY
              kind: socket
              socketPath: /run/packetnet/lobby.sock
            service:
              command: /usr/bin/python3
              args: [lobby.py, --socket, /run/packetnet/lobby.sock]
              environment:
                EXAMPLE_FLAG: "1"
                OTHER: two
              workingDirectory: /var/lib/packetnet/apps/lobby
              restart: on-failure
              managed: external
            ui:
              upstream: http://127.0.0.1:9090
              name: LOBBY
              icon: users
            """;

        var m = AppPackageManifestYaml.Parse(yaml);

        m.Manifest.Should().Be(1);
        m.Id.Should().Be("lobby");
        m.Name.Should().Be("LOBBY");
        m.Version.Should().Be("1.0.0");
        m.Description.Should().Contain("lobby");
        m.Icon.Should().Be("users");
        m.Capabilities.Should().Equal("session", "network");

        m.Session.Should().NotBeNull();
        m.Session!.Match.Should().Be("LOBBY");
        m.Session.Kind.Should().Be(ApplicationKind.Socket);
        m.Session.SocketPath.Should().Be("/run/packetnet/lobby.sock");

        m.Service.Should().NotBeNull();
        m.Service!.Command.Should().Be("/usr/bin/python3");
        m.Service.Args.Should().Equal("lobby.py", "--socket", "/run/packetnet/lobby.sock");
        m.Service.Environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["EXAMPLE_FLAG"] = "1",
            ["OTHER"] = "two",
        });
        m.Service.WorkingDirectory.Should().Be("/var/lib/packetnet/apps/lobby");
        m.Service.Restart.Should().Be(AppServiceRestart.OnFailure);
        m.Service.Managed.Should().Be(AppServiceManaged.External);

        m.Ui.Should().NotBeNull();
        m.Ui!.Upstream.Should().Be("http://127.0.0.1:9090");
        m.Ui.Name.Should().Be("LOBBY");
    }

    [Theory]
    [InlineData("on-failure", AppServiceRestart.OnFailure)]
    [InlineData("onFailure", AppServiceRestart.OnFailure)]
    [InlineData("OnFailure", AppServiceRestart.OnFailure)]
    [InlineData("always", AppServiceRestart.Always)]
    [InlineData("never", AppServiceRestart.Never)]
    public void Restart_binds_from_kebab_camel_and_pascal_forms(string text, AppServiceRestart expected)
    {
        var m = AppPackageManifestYaml.Parse($"""
            manifest: 1
            id: x
            service:
              command: /bin/x
              restart: {text}
            """);

        m.Service!.Restart.Should().Be(expected);
    }

    [Theory]
    [InlineData("pdn", AppServiceManaged.Pdn)]
    [InlineData("external", AppServiceManaged.External)]
    public void Managed_binds_from_lowercase_text(string text, AppServiceManaged expected)
    {
        var m = AppPackageManifestYaml.Parse($"""
            manifest: 1
            id: x
            service:
              command: /bin/x
              managed: {text}
            """);

        m.Service!.Managed.Should().Be(expected);
    }

    [Theory]
    [InlineData("process", ApplicationKind.Process)]
    [InlineData("socket", ApplicationKind.Socket)]
    public void Session_kind_reuses_ApplicationKind(string text, ApplicationKind expected)
    {
        var m = AppPackageManifestYaml.Parse($"""
            manifest: 1
            id: x
            session:
              match: X
              kind: {text}
              command: /bin/x
              socketPath: /run/x.sock
            """);

        m.Session!.Kind.Should().Be(expected);
    }

    [Fact]
    public void Defaults_are_the_contract_defaults_when_keys_are_omitted()
    {
        var m = AppPackageManifestYaml.Parse("""
            manifest: 1
            id: x
            session:
              match: X
              command: /bin/x
            service:
              command: /bin/x
            """);

        m.Session!.Kind.Should().Be(ApplicationKind.Process, "process is the spawn-per-connect floor");
        m.Service!.Restart.Should().Be(AppServiceRestart.OnFailure);
        m.Service.Managed.Should().Be(AppServiceManaged.Pdn);
        m.Service.Environment.Should().BeEmpty();
        m.Service.WorkingDirectory.Should().BeNull("null means the state dir, resolved by the supervisor");
        m.Name.Should().BeNull("the catalog/UI default the label to the id");
    }

    [Fact]
    public void Serialize_then_parse_round_trips_and_emits_kebab_enum_text()
    {
        var manifest = new AppPackageManifest
        {
            Manifest = 1,
            Id = "demo",
            Name = "DEMO",
            Capabilities = ["session"],
            Session = new AppSessionSpec
            {
                Match = "DEMO",
                Kind = ApplicationKind.Socket,
                SocketPath = "/run/packetnet/demo.sock",
            },
            Service = new AppServiceSpec
            {
                Command = "/usr/bin/python3",
                Args = ["demo.py"],
                Environment = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
                Restart = AppServiceRestart.OnFailure,
                Managed = AppServiceManaged.External,
            },
            Ui = new AppUiConfig { Upstream = "http://127.0.0.1:9091" },
        };

        var yaml = AppPackageManifestYaml.Serialize(manifest);

        yaml.Should().Contain("on-failure", "the contract spells the enum kebab-case")
            .And.Contain("external")
            .And.Contain("socket");

        var back = AppPackageManifestYaml.Parse(yaml);
        back.Should().BeEquivalentTo(manifest);
    }

    [Fact]
    public void Malformed_yaml_throws_a_descriptive_exception()
    {
        var act = () => AppPackageManifestYaml.Parse("id: [unclosed");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*pdn-app.yaml is not a valid manifest*");
    }

    [Fact]
    public void An_empty_document_throws_rather_than_yielding_a_default_manifest()
    {
        var act = () => AppPackageManifestYaml.Parse("# all comments, no manifest\n");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void An_unknown_enum_value_throws_naming_the_closed_set()
    {
        var act = () => AppPackageManifestYaml.Parse("""
            manifest: 1
            id: x
            service:
              command: /bin/x
              restart: sometimes
            """);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*'sometimes'*")
            .WithMessage("*on-failure*");
    }

    [Fact]
    public void Unknown_keys_are_ignored_for_forward_compatibility()
    {
        var m = AppPackageManifestYaml.Parse("""
            manifest: 1
            id: x
            futureTopLevelThing: true
            ui:
              upstream: http://127.0.0.1:9090
              futureNestedThing: [a, b]
            """);

        m.Id.Should().Be("x");
        m.Ui!.Upstream.Should().Be("http://127.0.0.1:9090");
    }
}
