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
    public void Parses_the_forward_block_with_defaults_and_kebab_tls()
    {
        var m = AppPackageManifestYaml.Parse("""
            manifest: 1
            id: mail
            service:
              command: /bin/mail
            forward:
              - listen: 993
                target: 127.0.0.1:1430
                tls: terminate
              - listen: 465
                target: 127.0.0.1:1465
              - listen: 4000
                target: localhost:4001
                tls: raw
            """);

        m.Forward.Should().HaveCount(3);
        m.Forward[0].Listen.Should().Be(993);
        m.Forward[0].Target.Should().Be("127.0.0.1:1430");
        m.Forward[0].Tls.Should().Be(ForwardTls.Terminate);
        m.Forward[1].Tls.Should().Be(ForwardTls.Terminate, "terminate is the default when tls is omitted");
        m.Forward[2].Tls.Should().Be(ForwardTls.Raw);
    }

    [Fact]
    public void The_forward_block_round_trips_through_serialize_and_parse()
    {
        var manifest = new AppPackageManifest
        {
            Manifest = 1,
            Id = "mail",
            Service = new AppServiceSpec { Command = "/bin/mail" },
            Forward =
            [
                new AppForwardSpec { Listen = 993, Target = "127.0.0.1:1430", Tls = ForwardTls.Terminate },
                new AppForwardSpec { Listen = 4000, Target = "::1:4001", Tls = ForwardTls.Raw },
            ],
        };

        var yaml = AppPackageManifestYaml.Serialize(manifest);
        yaml.Should().Contain("terminate").And.Contain("raw");

        var back = AppPackageManifestYaml.Parse(yaml);
        back.Should().BeEquivalentTo(manifest);
    }

    [Fact]
    public void An_unknown_tls_value_throws_naming_the_closed_set()
    {
        var act = () => AppPackageManifestYaml.Parse("""
            manifest: 1
            id: mail
            service:
              command: /bin/mail
            forward:
              - listen: 993
                target: 127.0.0.1:1430
                tls: passthrough
            """);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*'passthrough'*")
            .WithMessage("*terminate*");
    }

    [Fact]
    public void A_manifest_without_a_forward_block_defaults_to_empty()
    {
        var m = AppPackageManifestYaml.Parse("""
            manifest: 1
            id: x
            service:
              command: /bin/x
            """);

        m.Forward.Should().BeEmpty();
    }

    [Fact]
    public void Ui_mode_defaults_to_standalone_when_the_key_is_omitted()
    {
        var m = AppPackageManifestYaml.Parse("""
            manifest: 1
            id: x
            ui:
              upstream: http://127.0.0.1:9090
            """);

        m.Ui!.Mode.Should().Be(AppUiMode.Standalone, "standalone is the safe default — a full navigation");
    }

    [Theory]
    [InlineData("standalone", AppUiMode.Standalone)]
    [InlineData("embedded", AppUiMode.Embedded)]
    [InlineData("slot", AppUiMode.Slot)]
    [InlineData("Embedded", AppUiMode.Embedded)]
    [InlineData("SLOT", AppUiMode.Slot)]
    public void Ui_mode_binds_each_value_case_insensitively(string text, AppUiMode expected)
    {
        var m = AppPackageManifestYaml.Parse($"""
            manifest: 1
            id: x
            ui:
              upstream: http://127.0.0.1:9090
              mode: {text}
            """);

        m.Ui!.Mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("popup")]
    [InlineData("inline")]
    [InlineData("3")]
    public void An_unknown_ui_mode_falls_back_to_standalone_rather_than_erroring(string text)
    {
        // Unlike the strict manifest enums (restart / managed / tls), ui.mode is forgiving: an
        // unknown value (an app authored against a newer mode set) safe-defaults to standalone so
        // the whole manifest still loads. (docs/app-packages.md § UI surface modes.)
        var m = AppPackageManifestYaml.Parse($"""
            manifest: 1
            id: x
            ui:
              upstream: http://127.0.0.1:9090
              mode: {text}
            """);

        m.Ui!.Mode.Should().Be(AppUiMode.Standalone);
    }

    [Fact]
    public void Ui_mode_round_trips_through_serialize_and_parse_as_kebab_text()
    {
        var manifest = new AppPackageManifest
        {
            Manifest = 1,
            Id = "demo",
            Ui = new AppUiConfig { Upstream = "http://127.0.0.1:9090", Mode = AppUiMode.Slot },
        };

        var yaml = AppPackageManifestYaml.Serialize(manifest);
        yaml.Should().Contain("slot", "the mode is emitted as its lowercase contract name");

        AppPackageManifestYaml.Parse(yaml).Ui!.Mode.Should().Be(AppUiMode.Slot);
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
