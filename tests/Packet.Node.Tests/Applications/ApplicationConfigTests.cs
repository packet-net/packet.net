using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// The <c>applications:</c> registry binds from YAML and round-trips, and the validator
/// enforces its invariants: unique ids + match verbs, a process app needs a command, and a
/// match may not shadow a built-in console verb (so a registered app is never dead config).
/// </summary>
[Trait("Category", "Node")]
public sealed class ApplicationConfigTests
{
    private const string BaseIdentity = "identity:\n  callsign: M0LTE-1\n  alias: PDN\n";

    private static NodeConfig Valid(params ApplicationConfig[] apps) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1", Alias = "PDN" },
        Applications = apps,
    };

    private static FluentValidation.Results.ValidationResult Validate(NodeConfig cfg)
        => new NodeConfigValidator().Validate(cfg);

    [Fact]
    public void Applications_bind_from_yaml_with_args_and_default_kind()
    {
        var yaml = BaseIdentity + """
            applications:
              - id: wall
                command: WALL
                executable: /usr/bin/python3
                args: [ /usr/share/packetnet/apps/wall/wall.py ]
                workingDirectory: /var/lib/packetnet/apps/wall
                capabilities: [ session ]
            """;

        var cfg = NodeConfigYaml.Parse(yaml);

        var app = cfg.Applications.Should().ContainSingle().Subject;
        app.Id.Should().Be("wall");
        app.Command.Should().Be("WALL");
        app.Enabled.Should().BeTrue();                  // defaults true
        app.Kind.Should().Be(ApplicationKind.Process);  // defaults Process when kind: omitted
        app.Executable.Should().Be("/usr/bin/python3");
        app.Args.Should().Equal(["/usr/share/packetnet/apps/wall/wall.py"]);
        app.WorkingDirectory.Should().Be("/var/lib/packetnet/apps/wall");
        app.Capabilities.Should().Equal(["session"]);
    }

    [Fact]
    public void Explicit_kind_and_disabled_bind()
    {
        var yaml = BaseIdentity + """
            applications:
              - id: wall
                command: WALL
                enabled: false
                kind: process
                executable: /bin/cat
            """;

        var app = NodeConfigYaml.Parse(yaml).Applications.Should().ContainSingle().Subject;
        app.Enabled.Should().BeFalse();
        app.Kind.Should().Be(ApplicationKind.Process);
    }

    [Fact]
    public void Applications_round_trip_through_serialize_parse()
    {
        var cfg = Valid(new ApplicationConfig
        {
            Id = "wall",
            Command = "WALL",
            Executable = "/usr/bin/python3",
            Args = ["wall.py"],
            Capabilities = ["session"],
        });

        var round = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(cfg));

        var app = round.Applications.Should().ContainSingle().Subject;
        app.Id.Should().Be("wall");
        app.Command.Should().Be("WALL");
        app.Executable.Should().Be("/usr/bin/python3");
        app.Args.Should().Equal(["wall.py"]);
    }

    [Fact]
    public void Empty_applications_is_the_default_and_valid()
    {
        var cfg = Valid();
        cfg.Applications.Should().BeEmpty();
        Validate(cfg).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_well_formed_process_app_validates()
    {
        var cfg = Valid(new ApplicationConfig { Id = "wall", Command = "WALL", Executable = "/usr/bin/python3" });
        Validate(cfg).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var cfg = Valid(
            new ApplicationConfig { Id = "wall", Command = "WALL", Executable = "/bin/cat" },
            new ApplicationConfig { Id = "wall", Command = "GUEST", Executable = "/bin/cat" });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("WALL", "wall")]   // same verb, different case
    [InlineData("WALL", "WALL")]
    public void Duplicate_match_verbs_are_rejected_case_insensitively(string a, string b)
    {
        var cfg = Valid(
            new ApplicationConfig { Id = "a", Command = a, Executable = "/bin/cat" },
            new ApplicationConfig { Id = "b", Command = b, Executable = "/bin/cat" });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("BYE")]
    [InlineData("B")]        // an abbreviation of a built-in
    [InlineData("connect")]
    [InlineData("N")]        // Nodes
    [InlineData("?")]        // help
    [InlineData("SYSOP")]
    public void A_match_that_collides_with_a_builtin_verb_is_rejected(string match)
    {
        var cfg = Valid(new ApplicationConfig { Id = "x", Command = match, Executable = "/bin/cat" });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_process_app_without_a_command_is_rejected()
    {
        var cfg = Valid(new ApplicationConfig { Id = "wall", Command = "WALL", Executable = null });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_blank_match_is_rejected()
    {
        var cfg = Valid(new ApplicationConfig { Id = "wall", Command = "", Executable = "/bin/cat" });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    // ── The human-plane ui block (Slice 3) ──────────────────────────────

    [Fact]
    public void Ui_block_binds_from_yaml()
    {
        var yaml = BaseIdentity + """
            applications:
              - id: wall
                command: WALL
                executable: /usr/bin/python3
                ui:
                  upstream: http://127.0.0.1:9090
                  name: WALL
                  icon: message-square
            """;

        var app = NodeConfigYaml.Parse(yaml).Applications.Should().ContainSingle().Subject;
        app.Ui.Should().NotBeNull();
        app.Ui!.Upstream.Should().Be("http://127.0.0.1:9090");
        app.Ui.Name.Should().Be("WALL");
        app.Ui.Icon.Should().Be("message-square");
    }

    [Fact]
    public void A_well_formed_ui_app_validates()
    {
        var cfg = Valid(new ApplicationConfig
        {
            Id = "wall",
            Command = "WALL",
            Executable = "/bin/cat",
            Ui = new AppUiConfig { Upstream = "http://127.0.0.1:9090", Name = "WALL" },
        });
        Validate(cfg).IsValid.Should().BeTrue();
    }

    // ── The socket rung (Slice 2) ───────────────────────────────────────

    [Fact]
    public void A_socket_app_binds_from_yaml_and_validates()
    {
        var yaml = BaseIdentity + """
            applications:
              - id: lobby
                command: LOBBY
                kind: socket
                socketPath: /run/packetnet/lobby.sock
            """;

        var app = NodeConfigYaml.Parse(yaml).Applications.Should().ContainSingle().Subject;
        app.Kind.Should().Be(ApplicationKind.Socket);
        app.SocketPath.Should().Be("/run/packetnet/lobby.sock");
        Validate(new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Applications = [app],
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_socket_app_without_a_socket_path_is_rejected()
    {
        var cfg = Valid(new ApplicationConfig { Id = "lobby", Command = "LOBBY", Kind = ApplicationKind.Socket, SocketPath = null });
        Validate(cfg).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("127.0.0.1:9090")]   // no scheme
    [InlineData("ftp://127.0.0.1")]  // wrong scheme
    [InlineData("")]
    public void An_invalid_ui_upstream_is_rejected(string upstream)
    {
        var cfg = Valid(new ApplicationConfig
        {
            Id = "wall",
            Command = "WALL",
            Executable = "/bin/cat",
            Ui = new AppUiConfig { Upstream = upstream },
        });
        Validate(cfg).IsValid.Should().BeFalse();
    }
}
