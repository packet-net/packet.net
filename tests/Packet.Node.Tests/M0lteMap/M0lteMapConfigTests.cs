using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.M0lteMap;

/// <summary>
/// The UK packet map config (#453): the YAML an operator writes (GB7RDG's values as the worked
/// example), its survival through <c>pdn config export</c>/<c>import</c> (YAML) and the config
/// store (JSON), the defaults, and what the validator refuses.
/// </summary>
public sealed class M0lteMapConfigTests
{
    private const string Gb7rdgYaml = """
        schemaVersion: 2
        identity:
          callsign: GB7RDG
          alias: READNG
          grid: IO91lk
        m0lteMap:
          enabled: true
          comment: https://ukpacketradio.network/nodes:gb7rdg
        ports:
          - id: 2m
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8910
            m0lteMap:
              linkType: RF
              frequencyMhz: 144.9375
              modulation: QPSK
              baud: 1800
              bitrate: 3600
              usage: Mixed
              comment: 144.9375 FM QPSK3600 IL2Pc
          - id: 40m-bpsk
            transport:
              kind: kiss-tcp
              host: 10.45.0.37
              port: 8101
            m0lteMap:
              frequencyMhz: 7.0516
              modulation: BPSK300
              baud: 300
              bitrate: 600
              usage: Mixed
          - id: lab
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8102
            m0lteMap:
              enabled: false
          - id: plain
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8103
        """;

    private static readonly NodeConfigValidator Validator = new();

    [Fact]
    public void The_gb7rdg_example_parses_and_validates()
    {
        var config = NodeConfigYaml.Parse(Gb7rdgYaml);

        Validator.Validate(config).IsValid.Should().BeTrue();
        config.M0lteMap.Should().Be(new M0lteMapConfig { Enabled = true, Comment = "https://ukpacketradio.network/nodes:gb7rdg" });
        config.Ports[0].M0lteMap.Should().Be(new PortM0lteMapConfig
        {
            LinkType = M0lteMapLinkType.RF,
            FrequencyMhz = 144.9375m,
            Modulation = "QPSK",
            Baud = 1800,
            Bitrate = 3600,
            Usage = "Mixed",
            Comment = "144.9375 FM QPSK3600 IL2Pc",
        });
        config.Ports[1].M0lteMap!.LinkType.Should().BeNull();
        config.Ports[1].M0lteMap!.FrequencyMhz.Should().Be(7.0516m);
        config.Ports[1].M0lteMap!.Enabled.Should().BeTrue("a port is on the map unless it opts out, as in LinBPQ");
        config.Ports[2].M0lteMap!.Enabled.Should().BeFalse();
        config.Ports[3].M0lteMap.Should().BeNull();
    }

    [Fact]
    public void Export_then_import_gives_back_the_same_config()
    {
        var config = NodeConfigYaml.Parse(Gb7rdgYaml);

        // NodeConfig's lists compare by reference under record equality; compare the pieces.
        foreach (var back in new[]
                 {
                     NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config)),     // pdn config export / import
                     NodeConfigJson.Deserialize(NodeConfigJson.Serialize(config)), // the config store and the API
                 })
        {
            back.M0lteMap.Should().Be(config.M0lteMap);
            back.Ports.Should().Equal(config.Ports);
        }
    }

    [Fact]
    public void The_exported_yaml_writes_the_frequency_as_typed()
    {
        var yaml = NodeConfigYaml.Serialize(NodeConfigYaml.Parse(Gb7rdgYaml));

        yaml.Should().Contain("frequencyMhz: 144.9375").And.Contain("frequencyMhz: 7.0516").And.Contain("linkType: RF");
    }

    [Fact]
    public void Off_by_default_and_a_config_from_before_this_feature_loads_unchanged()
    {
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 2
            identity:
              callsign: M0LTE-1
            """);

        config.M0lteMap.Should().Be(new M0lteMapConfig());
        config.M0lteMap.Enabled.Should().BeFalse();
        config.M0lteMap.Comment.Should().BeNull();
    }

    [Theory]
    [InlineData("frequencyMhz: 0")]
    [InlineData("frequencyMhz: -144.9")]
    [InlineData("frequencyMhz: 144937500")]
    [InlineData("baud: -1")]
    [InlineData("bitrate: -1")]
    [InlineData("modulation: ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJ")]
    [InlineData("usage: ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJ")]
    public void A_bad_port_detail_is_refused_even_with_reporting_off(string line)
    {
        var config = NodeConfigYaml.Parse($"""
            schemaVersion: 2
            identity:
              callsign: M0LTE-1
            ports:
              - id: p
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                m0lteMap:
                  {line}
            """);

        var result = Validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("m0lteMap.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_link_type_number_is_refused()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports =
            [
                new PortConfig
                {
                    Id = "p",
                    Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 },
                    M0lteMap = new PortM0lteMapConfig { LinkType = (M0lteMapLinkType)99 },
                },
            ],
        };

        Validator.Validate(config).Errors.Should().Contain(e => e.ErrorMessage.Contains("m0lteMap.linkType", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_link_type_name_is_refused_at_parse()
    {
        var act = () => NodeConfigYaml.Parse("""
            schemaVersion: 2
            identity:
              callsign: M0LTE-1
            ports:
              - id: p
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                m0lteMap:
                  linkType: Satellite
            """);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void The_node_comment_is_capped_at_LinBpqs_MAPCOMMENT_length()
    {
        NodeConfig With(string comment) => new()
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            M0lteMap = new M0lteMapConfig { Comment = comment },
        };

        Validator.Validate(With(new string('a', 248))).IsValid.Should().BeTrue();
        Validator.Validate(With(new string('a', 249))).Errors
            .Should().Contain(e => e.ErrorMessage.Contains("m0lteMap.comment", StringComparison.Ordinal));
    }

    [Fact]
    public void Turning_reporting_on_or_off_is_itemised_in_the_apply_preview()
    {
        var off = NodeConfigYaml.Parse(Gb7rdgYaml) with { M0lteMap = new M0lteMapConfig() };
        var on = NodeConfigYaml.Parse(Gb7rdgYaml);

        Packet.Node.Core.Hosting.ReconcilePreviewBuilder.Build(off, on).Live
            .Should().ContainSingle(c => c.Path == "m0lteMap").Which.Summary.Should().Contain("turned on");
        Packet.Node.Core.Hosting.ReconcilePreviewBuilder.Build(on, off).Live
            .Should().ContainSingle(c => c.Path == "m0lteMap").Which.Summary.Should().Contain("turned off");
    }
}
