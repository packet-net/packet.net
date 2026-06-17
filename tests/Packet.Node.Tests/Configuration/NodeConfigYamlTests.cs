using Packet.Node.Core.Configuration;
using Packet.NetRom.Wire;

namespace Packet.Node.Tests.Configuration;

public class NodeConfigYamlTests
{
    [Fact]
    public void Parses_a_full_config_with_all_three_transport_kinds()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
              grid: IO91wm
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                ax25:
                  t1Ms: 3000
                  n2: 10
                  windowSize: 4
                kiss:
                  txDelay: 30
                  persistence: 63
              - id: hf
                enabled: false
                transport:
                  kind: serial-kiss
                  device: /dev/ttyACM0
                  baud: 57600
              - id: nino
                enabled: true
                transport:
                  kind: nino-tnc
                  device: /dev/ttyACM1
                  baud: 921600
                  mode: 6
            services:
              banner: "Hi {node}"
              prompt: "{call}> "
            management:
              telnet:
                enabled: true
                bind: 127.0.0.1
                port: 8011
              http:
                bind: 0.0.0.0
                port: 8080
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.Identity.Callsign.Should().Be("M0LTE-1");
        config.Identity.Alias.Should().Be("LONDON");
        config.Ports.Should().HaveCount(3);

        var vhf = config.Ports[0];
        vhf.Id.Should().Be("vhf");
        vhf.Enabled.Should().BeTrue();
        vhf.Transport.Should().BeOfType<KissTcpTransport>()
            .Which.Port.Should().Be(8001);
        vhf.Ax25!.T1Ms.Should().Be(3000);
        vhf.Kiss!.TxDelay.Should().Be((byte)30);

        config.Ports[1].Transport.Should().BeOfType<SerialKissTransport>()
            .Which.Device.Should().Be("/dev/ttyACM0");

        var nino = config.Ports[2].Transport.Should().BeOfType<NinoTncTransport>().Subject;
        nino.Mode.Should().Be(6);
        nino.Baud.Should().Be(921600);

        config.Management.Telnet.Port.Should().Be(8011);
        config.Management.Http.Bind.Should().Be("0.0.0.0");

        // No netrom: block → the default (enabled, canonical knobs).
        config.NetRom.Enabled.Should().BeTrue();
        config.NetRom.MinQuality.Should().BeNull();
    }

    [Fact]
    public void Parses_and_round_trips_per_port_n1_paclen_and_netrom_quality()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                ax25:
                  n1: 80
                netRomQuality: 191
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8002
            """;

        var config = NodeConfigYaml.Parse(yaml);

        var hf = config.Ports[0];
        hf.Ax25!.N1.Should().Be(80);
        hf.NetRomQuality.Should().Be(191);
        // The effective resolution falls back to the global default then 192.
        hf.EffectiveNetRomQuality(globalDefault: 200).Should().Be(191);   // explicit wins

        var vhf = config.Ports[1];
        vhf.Ax25.Should().BeNull();           // N1 unset ⇒ engine default 256
        vhf.NetRomQuality.Should().BeNull();   // inherits the global default
        vhf.EffectiveNetRomQuality(globalDefault: 200).Should().Be(200);
        vhf.EffectiveNetRomQuality(globalDefault: null).Should().Be(192);

        // Survives a serialise → re-parse round trip.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Ax25!.N1.Should().Be(80);
        reparsed.Ports[0].NetRomQuality.Should().Be(191);
        reparsed.Ports[1].NetRomQuality.Should().BeNull();
    }

    [Fact]
    public void Parses_a_netrom_block_with_overridden_knobs()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              broadcast: true
              connect: true
              alias: NODE
              defaultNeighbourQuality: 203
              minQuality: 150
              obsoleteInitial: 5
              obsoleteMinimum: 3
              sweepIntervalSeconds: 1800
              window: 7
              transportTimeoutSeconds: 8
              transportRetries: 5
              timeToLive: 30
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.NetRom.Enabled.Should().BeTrue();
        config.NetRom.Broadcast.Should().BeTrue();
        // Legacy connect: key still parses (back-compat) and resolves to Transit
        // (connect:true with forward defaulting on).
        config.NetRom.Connect.Should().BeTrue();
        config.NetRom.EffectiveRouting.Should().Be(NetRomRouting.Transit);
        config.NetRom.Alias.Should().Be("NODE");
        config.NetRom.DefaultNeighbourQuality.Should().Be(203);
        config.NetRom.MinQuality.Should().Be(150);
        config.NetRom.ObsoleteInitial.Should().Be(5);
        config.NetRom.ObsoleteMinimum.Should().Be(3);
        config.NetRom.SweepIntervalSeconds.Should().Be(1800);
        config.NetRom.Window.Should().Be(7);
        config.NetRom.TransportTimeoutSeconds.Should().Be(8);
        config.NetRom.TransportRetries.Should().Be(5);
        config.NetRom.TimeToLive.Should().Be(30);
    }

    [Fact]
    public void Netrom_broadcast_off_and_routing_none_by_default()
    {
        // TX-bearing NET/ROM is opt-in: a stock node hears but does not transmit
        // NODES or open circuits until the operator turns them on. With no keys set,
        // broadcast is off and the routing role resolves to None (passive).
        var config = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        config.NetRom.Broadcast.Should().BeFalse();
        config.NetRom.Routing.Should().BeNull("the routing: key is absent");
        config.NetRom.Connect.Should().BeNull("the legacy connect: key is absent");
        config.NetRom.Forward.Should().BeNull("the legacy forward: key is absent");
        config.NetRom.EffectiveRouting.Should().Be(NetRomRouting.None);
    }

    [Fact]
    public void Netrom_parses_the_new_routing_knob_and_round_trips()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              routing: transit
            """;

        var config = NodeConfigYaml.Parse(yaml);
        config.NetRom.Routing.Should().Be(NetRomRouting.Transit);
        config.NetRom.EffectiveRouting.Should().Be(NetRomRouting.Transit);

        // Round-trips: serialise → parse preserves the explicit routing knob.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.NetRom.Routing.Should().Be(NetRomRouting.Transit);
    }

    [Theory]
    [InlineData("endpoint", NetRomRouting.Endpoint)]
    [InlineData("transit", NetRomRouting.Transit)]
    [InlineData("none", NetRomRouting.None)]
    public void Netrom_routing_knob_parses_each_mode_case_insensitively(string text, NetRomRouting expected)
    {
        var yaml = $"identity:\n  callsign: M0LTE-1\nnetRom:\n  enabled: true\n  routing: {text}\n";
        NodeConfigYaml.Parse(yaml).NetRom.Routing.Should().Be(expected);
    }

    [Fact]
    public void Netrom_can_be_disabled()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: false
            """;

        NodeConfigYaml.Parse(yaml).NetRom.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Management_auth_defaults_off_and_round_trips_when_enabled()
    {
        // Default-off: an absent management.auth block leaves auth disabled — the
        // no-regression contract for the auth foundation.
        var defaulted = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        defaulted.Management.Auth.Enabled.Should().BeFalse();
        defaulted.Management.Auth.AccessTokenMinutes.Should().BeNull();

        // And an explicit on round-trips through serialise→parse.
        const string yaml = """
            identity:
              callsign: M0LTE-1
            management:
              auth:
                enabled: true
                accessTokenMinutes: 30
            """;
        var parsed = NodeConfigYaml.Parse(yaml);
        parsed.Management.Auth.Enabled.Should().BeTrue();
        parsed.Management.Auth.AccessTokenMinutes.Should().Be(30);

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(parsed));
        reparsed.Management.Auth.Should().Be(parsed.Management.Auth);
    }

    [Theory]
    [InlineData("serial-kiss")]
    [InlineData("nino-tnc")]
    [InlineData("kiss-tcp")]
    [InlineData("axudp")]
    public void Round_trips_each_transport_kind_through_serialise_then_parse(string kind)
    {
        TransportConfig transport = kind switch
        {
            "serial-kiss" => new SerialKissTransport { Device = "/dev/ttyUSB0", Baud = 115200 },
            "nino-tnc" => new NinoTncTransport { Device = "/dev/ttyACM3", Baud = 57600, Mode = 9 },
            "axudp" => new AxudpTransport { Host = "peer.local", Port = 10093, LocalPort = 10093 },
            _ => new KissTcpTransport { Host = "modem.local", Port = 8100 },
        };
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "G7XYZ-7" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = transport }],
        };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.Ports.Should().HaveCount(1);
        reparsed.Ports[0].Transport.Should().Be(transport);
        reparsed.Identity.Callsign.Should().Be("G7XYZ-7");
    }

    [Fact]
    public void Parses_an_axudp_transport_with_all_fields()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                enabled: true
                transport:
                  kind: axudp
                  host: 10.0.0.2
                  port: 10093
                  localPort: 10093
            """;

        var config = NodeConfigYaml.Parse(yaml);

        var axudp = config.Ports.Should().ContainSingle().Subject
            .Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.Host.Should().Be("10.0.0.2");
        axudp.Port.Should().Be(10093);
        axudp.LocalPort.Should().Be(10093);
        axudp.DescribeEndpoint().Should().Be("axudp:10.0.0.2:10093(local:10093)");
    }

    [Fact]
    public void Axudp_localPort_defaults_to_ephemeral_when_omitted()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                transport:
                  kind: axudp
                  host: peer.example
                  port: 10093
            """;

        var axudp = NodeConfigYaml.Parse(yaml).Ports[0].Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.LocalPort.Should().Be(0, "localPort defaults to 0 (ephemeral) when omitted");
    }

    [Fact]
    public void Axudp_tolerates_a_stale_includeFcs_key_from_a_pre_removal_config()
    {
        // 'includeFcs' was removed (AXUDP always carries the FCS — the FCS-less
        // opt-out interoperated with nothing; see docs/strict-vs-pragmatic-audit.md).
        // A config carrying the now-defunct key must still load: the transport
        // converter reads only the fields it knows, so a stale 'includeFcs:' is
        // ignored, not a parse error.
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                transport:
                  kind: axudp
                  host: 10.0.0.2
                  port: 10093
                  localPort: 10093
                  includeFcs: false
            """;

        var act = () => NodeConfigYaml.Parse(yaml);
        act.Should().NotThrow("a stale includeFcs key is ignored, so a pre-removal config still loads");
        var axudp = act().Ports[0].Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.Host.Should().Be("10.0.0.2");
        axudp.Port.Should().Be(10093);
        axudp.LocalPort.Should().Be(10093);
    }

    [Fact]
    public void Unknown_transport_kind_throws_a_clear_parse_error()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE
            ports:
              - id: bad
                transport:
                  kind: smoke-signals
                  host: 10.0.0.1
                  port: 10093
            """;

        var act = () => NodeConfigYaml.Parse(yaml);
        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain("smoke-signals");
    }

    [Fact]
    public void Parses_and_round_trips_a_ports_channel_profile()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                profile: slow-afsk1200
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
            """;

        var config = NodeConfigYaml.Parse(yaml);
        config.Ports[0].Profile.Should().Be("slow-afsk1200");

        // Round-trip: the profile survives serialise → parse.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Profile.Should().Be("slow-afsk1200");
    }

    [Fact]
    public void Empty_or_comment_only_document_parses_to_a_default_shape_not_null()
    {
        var config = NodeConfigYaml.Parse("# just a comment\n");
        config.Should().NotBeNull();
        config.Ports.Should().BeEmpty();
    }

    [Fact]
    public void Netrom_inp3_defaults_to_disabled_when_no_inp3_block()
    {
        // The default-off proof at the config layer: a config with no inp3: block at
        // all yields Inp3 == NetRomInp3Options.Default ⇒ Enabled == false, so the
        // host creates no Inp3Host and behaves byte-for-byte as today.
        var config = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        config.NetRom.Inp3.Should().NotBeNull();
        config.NetRom.Inp3.Enabled.Should().BeFalse();
        config.NetRom.Inp3.Should().Be(NetRomInp3Options.Default);
    }

    [Fact]
    public void Parses_a_netrom_inp3_block_with_all_knobs()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              inp3:
                enabled: true
                preferInp3Routes: true
                snttGainShift: 4
                probeUnknownCapability: false
                advertiseIpAccept: 4
                capabilityTextWidth: 12
                hopLimit: 20
                worsenThresholdMs: 750
                l3RttInterval: 00:01:30
                l3RttResetWindow: 00:05:00
                rifInterval: 00:10:00
                positiveDebounce: 00:00:03
            """;

        var inp3 = NodeConfigYaml.Parse(yaml).NetRom.Inp3;

        inp3.Enabled.Should().BeTrue();
        inp3.PreferInp3Routes.Should().BeTrue();
        inp3.SnttGainShift.Should().Be(4);
        inp3.ProbeUnknownCapability.Should().BeFalse();
        inp3.AdvertiseIpAccept.Should().Be(4);
        inp3.CapabilityTextWidth.Should().Be(12);
        inp3.HopLimit.Should().Be(20);
        inp3.WorsenThresholdMs.Should().Be(750);
        inp3.L3RttInterval.Should().Be(TimeSpan.FromSeconds(90));
        inp3.L3RttResetWindow.Should().Be(TimeSpan.FromMinutes(5));
        inp3.RifInterval.Should().Be(TimeSpan.FromMinutes(10));
        inp3.PositiveDebounce.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Round_trips_a_netrom_inp3_block_through_serialise_then_parse()
    {
        // A populated inp3: overlay survives serialise → parse intact — including the
        // TimeSpan-typed duration knobs (via YamlDotNet's built-in TimeSpan converter)
        // and the nullable advertiseIpAccept.
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Inp3 = new NetRomInp3Options
                {
                    Enabled = true,
                    PreferInp3Routes = true,
                    SnttGainShift = 5,
                    ProbeUnknownCapability = false,
                    AdvertiseIpAccept = 6,
                    CapabilityTextWidth = 10,
                    HopLimit = 15,
                    WorsenThresholdMs = 1500,
                    L3RttInterval = TimeSpan.FromSeconds(45),
                    L3RttResetWindow = TimeSpan.FromSeconds(200),
                    RifInterval = TimeSpan.FromSeconds(600),
                    PositiveDebounce = TimeSpan.FromSeconds(7),
                },
            },
        };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.NetRom.Inp3.Should().Be(original.NetRom.Inp3,
            "the whole inp3 overlay should round-trip\nYAML:\n{0}", yaml);
    }

    [Fact]
    public void Round_trips_a_default_disabled_inp3_overlay()
    {
        // The common case: INP3 left at its default (disabled). Serialising the
        // default config and parsing it back must still yield the disabled default —
        // OmitNull drops advertiseIpAccept, and the rest is the record default.
        var original = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.NetRom.Inp3.Should().Be(NetRomInp3Options.Default,
            "the default disabled overlay should round-trip\nYAML:\n{0}", yaml);
        reparsed.NetRom.Inp3.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Parses_a_traffic_block()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            traffic:
              enabled: false
              path: /tmp/frames.db
              retentionDays: 7
              maxMb: 64
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.Traffic.Should().Be(new TrafficConfig
        {
            Enabled = false,
            Path = "/tmp/frames.db",
            RetentionDays = 7,
            MaxMb = 64,
        });
    }

    [Fact]
    public void An_absent_traffic_block_means_the_on_by_default_record()
    {
        // Existing configs have no traffic: key — they must come up logging with
        // the default bounds (enabled is the whole point of the feature).
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            """);

        config.Traffic.Should().Be(new TrafficConfig());
        config.Traffic.Enabled.Should().BeTrue();
        config.Traffic.RetentionDays.Should().Be(14);
        config.Traffic.MaxMb.Should().Be(512);
        config.Traffic.Path.Should().BeNull("null = traffic.db beside pdn.db");
    }

    [Fact]
    public void Template_ships_the_default_traffic_block_and_round_trips()
    {
        // The first-start template documents the block with its defaults spelled
        // out; parsing it must yield exactly the record defaults, and the parsed
        // config must serialise→parse back to the same traffic block.
        var fromTemplate = NodeConfigYaml.Parse(NodeConfigTemplate.Yaml);
        fromTemplate.Traffic.Should().Be(new TrafficConfig(), "the template's traffic block must match the record defaults");

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(fromTemplate));
        reparsed.Traffic.Should().Be(fromTemplate.Traffic);
    }

    [Fact]
    public void Kiss_ackMode_parses_and_round_trips_when_set_true()
    {
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  txDelay: 30
                  ackMode: true
            """);

        config.Ports[0].Kiss!.AckMode.Should().BeTrue();
        config.Ports[0].Kiss!.TxDelay.Should().Be((byte)30);

        // The flag survives a serialise→parse round-trip unchanged.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Kiss!.AckMode.Should().BeTrue();
    }

    [Fact]
    public void Kiss_t1FromTxComplete_parses_and_round_trips_when_set_true()
    {
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  ackMode: true
                  t1FromTxComplete: true
            """);

        config.Ports[0].Kiss!.T1FromTxComplete.Should().BeTrue();

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Kiss!.T1FromTxComplete.Should().BeTrue();
    }

    [Fact]
    public void Kiss_t1FromTxComplete_defaults_false_when_absent()
    {
        // Pre-feature configs keep enqueue-time T1 semantics — no behaviour change.
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  txDelay: 30
            """);

        config.Ports[0].Kiss!.T1FromTxComplete.Should().BeFalse();
    }

    [Fact]
    public void Kiss_ackMode_defaults_false_when_absent()
    {
        // A port with KISS knobs set but no ackMode key must default the flag off —
        // the no-regression contract (a pre-feature config blasts fire-and-forget).
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  txDelay: 30
            """);

        config.Ports[0].Kiss!.AckMode.Should().BeFalse();
    }

    // ---- tailscale: (network-access.md S1 — parsed/validated, inert) ----------------

    [Fact]
    public void Parses_a_full_tailscale_block()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            tailscale:
              enabled: true
              authKeyFile: /etc/packetnet/tailscale.authkey
              hostname: rdg-pdn
              tags:
                - tag:server
                - tag:packetnet
              stateDir: /var/lib/packetnet/tsnet
              target: 127.0.0.1:8080
              funnel: true
            """;

        var ts = NodeConfigYaml.Parse(yaml).Tailscale;

        ts.Enabled.Should().BeTrue();
        ts.AuthKey.Should().BeNull();
        ts.AuthKeyFile.Should().Be("/etc/packetnet/tailscale.authkey");
        ts.Hostname.Should().Be("rdg-pdn");
        ts.Tags.Should().Equal("tag:server", "tag:packetnet");
        ts.StateDir.Should().Be("/var/lib/packetnet/tsnet");
        ts.Target.Should().Be("127.0.0.1:8080");
        ts.Funnel.Should().BeTrue();
    }

    [Fact]
    public void An_absent_tailscale_block_means_the_disabled_default_record()
    {
        // Existing configs have no tailscale: key — they must come up disabled (HTTP-only)
        // with the documented defaults.
        var config = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");

        config.Tailscale.Should().Be(new TailscaleConfig());
        config.Tailscale.Enabled.Should().BeFalse();
        config.Tailscale.Hostname.Should().Be("", "an empty hostname derives <callsign>-pdn");
        config.Tailscale.Tags.Should().BeEmpty();
        config.Tailscale.StateDir.Should().Be("/var/lib/packetnet/tsnet");
        config.Tailscale.Target.Should().Be("127.0.0.1:8080");
        config.Tailscale.Funnel.Should().BeFalse();
    }

    [Fact]
    public void Round_trips_a_tailscale_block_with_tags_through_serialise_then_parse()
    {
        // The list-aware Equals/GetHashCode (mirroring WebAuthnConfig.AllowedOrigins)
        // makes the round-trip value-equal even though serialise→parse yields a fresh
        // Tags list.
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Tailscale = new TailscaleConfig
            {
                Enabled = true,
                AuthKey = "tskey-abc123",
                Hostname = "rdg-pdn",
                Tags = ["tag:server"],
                StateDir = "/var/lib/packetnet/tsnet",
                Target = "127.0.0.1:8080",
                Funnel = false,
            },
        };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.Tailscale.Should().Be(original.Tailscale,
            "the whole tailscale block should round-trip\nYAML:\n{0}", yaml);
    }

    [Fact]
    public void Parses_app_packet_identity_fields_on_apps_and_applications()
    {
        // docs/app-packages.md § Application packet identity — the owner's per-app callsign pin,
        // opt-in NET/ROM advert, and command verb override on both apps[] (package overrides) and
        // applications[] (inline). The inline app carries a separate verb (command) and executable.
        const string yaml = """
            identity:
              callsign: M0LTE-1
            applications:
              - id: myapp
                command: MYAPP
                executable: /usr/bin/python3
                args: [app.py]
                callsign: M0LTE-3
                netrom:
                  alias: RDGCHAT
                  quality: 200
            apps:
              - id: bbs
                enabled: true
                command: BBS
                callsign: M9YYY-1
                netrom:
                  alias: RDGBBS
                  quality: 255
            """;

        var config = NodeConfigYaml.Parse(yaml);

        var inline = config.Applications.Should().ContainSingle().Subject;
        inline.Command.Should().Be("MYAPP");                 // the verb
        inline.Executable.Should().Be("/usr/bin/python3");   // the exec — distinct field
        inline.Callsign.Should().Be("M0LTE-3");
        inline.Netrom!.Alias.Should().Be("RDGCHAT");
        inline.Netrom!.Quality.Should().Be(200);

        var pkg = config.Apps.Should().ContainSingle().Subject;
        pkg.Enabled.Should().BeTrue();
        pkg.Command.Should().Be("BBS");                      // the verb override
        pkg.Callsign.Should().Be("M9YYY-1");                 // the pin
        pkg.Netrom!.Alias.Should().Be("RDGBBS");
        pkg.Netrom!.Quality.Should().Be(255);
    }

    [Fact]
    public void Round_trips_the_app_packet_identity_fields()
    {
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Applications =
            [
                new ApplicationConfig
                {
                    Id = "myapp", Command = "MYAPP", Executable = "/bin/cat",
                    Callsign = "M0LTE-3", Netrom = new AppNetromConfig { Alias = "RDGCHAT", Quality = 200 },
                },
            ],
            Apps =
            [
                new AppOverrideConfig
                {
                    Id = "bbs", Enabled = true, Command = "BBS", Callsign = "M9YYY-1",
                    Netrom = new AppNetromConfig { Alias = "RDGBBS", Quality = 255 },
                },
            ],
        };

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(original));

        reparsed.Applications.Should().BeEquivalentTo(original.Applications);
        reparsed.Apps.Should().BeEquivalentTo(original.Apps);
    }
}
