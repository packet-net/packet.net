using Packet.Node.Core.Configuration;
using Packet.NetRom.Wire;

namespace Packet.Node.Tests.Configuration;

public class NodeConfigValidatorTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig Valid(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = ports,
    };

    private static PortConfig TcpPort(string id, string host = "127.0.0.1", int port = 8001, bool enabled = true) => new()
    {
        Id = id,
        Enabled = enabled,
        Transport = new KissTcpTransport { Host = host, Port = port },
    };

    [Fact]
    public void Accepts_a_minimal_idle_node_with_no_ports()
    {
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_node_with_a_valid_port()
    {
        Validator.Validate(Valid(TcpPort("vhf"))).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("M0LTE-1", true)]
    [InlineData("G7XYZ", true)]
    [InlineData("M0LTE-15", true)]
    [InlineData("M0LTE-16", false)]   // SSID out of 0..15
    [InlineData("TOOLONGCALL", false)]
    [InlineData("lower", false)]      // lowercase not allowed by Callsign
    [InlineData("", false)]
    public void Callsign_acceptance_pairs_with_Callsign_TryParse(string callsign, bool expectValid)
    {
        var config = new NodeConfig { Identity = new Identity { Callsign = callsign } };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(false, false, true)]   // both off — fine
    [InlineData(false, true, true)]    // auth on, oauth off — fine
    [InlineData(true, true, true)]     // oauth on AND auth on — fine
    [InlineData(true, false, false)]   // oauth on but auth off — refused (tokens unenforced)
    public void Mcp_oauth_requires_management_auth(bool oauthEnabled, bool authEnabled, bool expectValid)
    {
        var config = Valid() with
        {
            Mcp = new McpConfig { Oauth = new McpOauthConfig { Enabled = oauthEnabled } },
            Management = new ManagementConfig { Auth = new AuthConfig { Enabled = authEnabled } },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Rejects_duplicate_port_ids_but_accepts_distinct_ones()
    {
        Validator.Validate(Valid(TcpPort("dup", port: 1), TcpPort("dup", port: 2)))
            .IsValid.Should().BeFalse();
        Validator.Validate(Valid(TcpPort("a", port: 1), TcpPort("b", port: 2)))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_two_ports_on_the_same_endpoint_but_accepts_distinct_endpoints()
    {
        // Same host:port on two ports — a device collision.
        Validator.Validate(Valid(TcpPort("a", "10.0.0.1", 9000), TcpPort("b", "10.0.0.1", 9000)))
            .IsValid.Should().BeFalse();
        Validator.Validate(Valid(TcpPort("a", "10.0.0.1", 9000), TcpPort("b", "10.0.0.1", 9001)))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 8443, "0.0.0.0", true)]   // enabled + valid
    [InlineData(true, 0, "0.0.0.0", false)]     // bad port
    [InlineData(true, 8443, "", false)]         // empty bind
    [InlineData(false, 0, "", true)]            // disabled → not validated at all
    public void Https_is_validated_only_when_enabled(bool enabled, int port, string bind, bool expectValid)
    {
        var config = Valid() with
        {
            Management = new ManagementConfig { Https = new HttpsConfig { Enabled = enabled, Port = port, Bind = bind } },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Https_cannot_collide_with_the_http_listener()
    {
        // Same address:port as the default http listener (127.0.0.1:8080) → rejected.
        Validator.Validate(Valid() with
        {
            Management = new ManagementConfig { Https = new HttpsConfig { Enabled = true, Bind = "127.0.0.1", Port = 8080 } },
        }).IsValid.Should().BeFalse();
        // A distinct port is fine.
        Validator.Validate(Valid() with
        {
            Management = new ManagementConfig { Https = new HttpsConfig { Enabled = true, Bind = "127.0.0.1", Port = 8443 } },
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Https_requires_a_cert_path_when_self_signed_generation_is_off()
    {
        Validator.Validate(Valid() with
        {
            Management = new ManagementConfig { Https = new HttpsConfig { Enabled = true, GenerateSelfSignedOnMissing = false } },
        }).IsValid.Should().BeFalse();
        Validator.Validate(Valid() with
        {
            Management = new ManagementConfig { Https = new HttpsConfig { Enabled = true, GenerateSelfSignedOnMissing = false, CertificatePath = "/etc/packetnet/server.pfx" } },
        }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, true)]    // both default → fine (60 < 10080)
    [InlineData(60, null, true)]      // access set, refresh default → fine
    [InlineData(null, 10080, true)]   // refresh set, access default → fine
    [InlineData(60, 10080, true)]     // refresh > access → fine
    [InlineData(60, 60, false)]       // refresh == access → reject
    [InlineData(60, 30, false)]       // refresh < access → reject
    [InlineData(0, null, false)]      // access must be positive
    [InlineData(null, 0, false)]      // refresh must be positive
    public void Auth_token_lifetimes_are_positive_and_refresh_outlives_access(
        int? accessMinutes, int? refreshMinutes, bool expectValid)
    {
        var config = Valid() with
        {
            Management = new ManagementConfig
            {
                Auth = new AuthConfig { AccessTokenMinutes = accessMinutes, RefreshTokenMinutes = refreshMinutes },
            },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("localhost", true)]                 // the zero-config default
    [InlineData("pdn.lab.example", true)]           // a real registrable domain
    [InlineData("", false)]                         // RP id is required
    [InlineData("192.168.0.10", false)]             // an IP literal is NOT a legal RP id
    [InlineData("::1", false)]                      // nor an IPv6 literal
    public void WebAuthn_rp_id_must_be_a_domain_not_an_ip(string rpId, bool expectValid)
    {
        var config = Valid() with
        {
            Management = new ManagementConfig
            {
                Auth = new AuthConfig { WebAuthn = new WebAuthnConfig { RelyingPartyId = rpId } },
            },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("https://pdn.lab.example:8443", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("pdn.lab.example", false)]          // not an absolute origin
    [InlineData("ftp://pdn.lab.example", false)]    // not http(s)
    public void WebAuthn_allowed_origins_must_be_absolute_http_origins(string origin, bool expectValid)
    {
        var config = Valid() with
        {
            Management = new ManagementConfig
            {
                Auth = new AuthConfig { WebAuthn = new WebAuthnConfig { AllowedOrigins = [origin] } },
            },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(0, false)]    // baud must be > 0
    [InlineData(1, true)]
    [InlineData(57600, true)]
    public void Serial_baud_must_be_positive(int baud, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "s",
            Transport = new SerialKissTransport { Device = "/dev/ttyACM0", Baud = baud },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void Nino_mode_must_be_in_0_to_15(int mode, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "n",
            Transport = new NinoTncTransport { Device = "/dev/ttyACM0", Baud = 57600, Mode = mode },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(0, false)]      // port out of 1..65535
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(70000, false)]
    public void KissTcp_port_must_be_in_range(int port, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "t",
            Transport = new KissTcpTransport { Host = "h", Port = port },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(0, false)]       // remote port out of 1..65535
    [InlineData(1, true)]
    [InlineData(10093, true)]
    [InlineData(65535, true)]
    [InlineData(70000, false)]
    public void Axudp_remote_port_must_be_in_range(int port, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "a",
            Transport = new AxudpTransport { Host = "peer", Port = port, LocalPort = 10093 },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(-1, false)]      // localPort 0 is legal (ephemeral); negative is not
    [InlineData(0, true)]
    [InlineData(10093, true)]
    [InlineData(70000, false)]
    public void Axudp_localPort_allows_zero_ephemeral_but_must_be_in_range(int localPort, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "a",
            Transport = new AxudpTransport { Host = "peer", Port = 10093, LocalPort = localPort },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Axudp_requires_a_host()
    {
        var config = Valid(new PortConfig
        {
            Id = "a",
            Transport = new AxudpTransport { Host = "", Port = 10093 },
        });
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]              // no profile = spec defaults = valid
    [InlineData("", true)]               // blank = no profile = valid
    [InlineData("slow-afsk1200", true)]  // the known profile
    [InlineData("SLOW_AFSK1200", true)]  // case- + separator-insensitive
    [InlineData("turbo", false)]         // unknown profile = config error
    public void Profile_must_be_a_known_name_or_absent(string? profile, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Profile = profile,
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(null, true)]         // no preset = lenient = valid
    [InlineData("", true)]          // blank = lenient = valid
    [InlineData("strict", true)]
    [InlineData("lenient", true)]
    [InlineData("bpq", true)]
    [InlineData("xrouter", true)]
    [InlineData("direwolf", true)]
    [InlineData("Direwolf", true)]  // case-insensitive
    [InlineData("kenwood", false)]  // unknown preset = config error, not a silent default
    public void Compat_preset_must_be_a_known_name_or_absent(string? preset, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Compat = new PortCompatConfig { Preset = preset },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(null, true)]                 // no selector = default quirks = valid
    [InlineData("default", true)]
    [InlineData("strictly-faithful", true)]
    [InlineData("StrictlyFaithful", true)]   // case- + separator-insensitive
    [InlineData("faithful", false)]          // unknown selector = config error
    public void Compat_quirks_must_be_a_known_selector_or_absent(string? quirks, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Compat = new PortCompatConfig { Quirks = quirks },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Rejects_out_of_range_ax25_window()
    {
        var bad = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Ax25 = new Ax25PortParams { WindowSize = 200 },
        });
        Validator.Validate(bad).IsValid.Should().BeFalse();

        var ok = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Ax25 = new Ax25PortParams { WindowSize = 7 },
        });
        Validator.Validate(ok).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(80, true)]     // an HF-friendly PACLEN
    [InlineData(256, true)]    // the AX.25 v2.2 ceiling
    [InlineData(16, true)]     // the floor
    [InlineData(15, false)]    // below the floor
    [InlineData(257, false)]   // above the ceiling
    [InlineData(0, false)]
    public void Validates_per_port_n1_paclen_range(int n1, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Ax25 = new Ax25PortParams { N1 = n1 },
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Accepts_an_unset_n1_preserving_the_default()
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            Ax25 = new Ax25PortParams { N2 = 8 },   // N1 unset
        });
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(191, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    [InlineData(-1, false)]
    public void Validates_per_port_netrom_quality_range(int quality, bool expectValid)
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            NetRomQuality = quality,
        });
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Accepts_an_unset_per_port_netrom_quality()
    {
        var config = Valid(new PortConfig
        {
            Id = "p",
            Transport = new KissTcpTransport { Host = "h", Port = 1 },
            // NetRomQuality unset — inherits the global default.
        });
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_telnet_port_out_of_range()
    {
        var config = Valid() with
        {
            Management = new ManagementConfig { Telnet = new TelnetConfig { Port = 0 } },
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Accepts_netrom_knobs_in_range_and_rejects_out_of_range()
    {
        var ok = Valid() with
        {
            NetRom = new NetRomConfig
            {
                Enabled = true,
                DefaultNeighbourQuality = 203,
                MinQuality = 150,
                ObsoleteInitial = 6,
                SweepIntervalSeconds = 1800,
            },
        };
        Validator.Validate(ok).IsValid.Should().BeTrue();

        Validator.Validate(Valid() with { NetRom = new NetRomConfig { MinQuality = 256 } })
            .IsValid.Should().BeFalse("quality must be in 0..255");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { ObsoleteInitial = 0 } })
            .IsValid.Should().BeFalse("OBSINIT must be positive");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { SweepIntervalSeconds = -1 } })
            .IsValid.Should().BeFalse("sweep interval must be positive");
    }

    [Fact]
    public void Accepts_netrom_l3l4_knobs_in_range_and_rejects_out_of_range()
    {
        var ok = Valid() with
        {
            NetRom = new NetRomConfig
            {
                Enabled = true, Broadcast = true, Connect = true,
                ObsoleteMinimum = 4, Window = 7, TransportTimeoutSeconds = 8,
                TransportRetries = 5, TimeToLive = 30,
            },
        };
        Validator.Validate(ok).IsValid.Should().BeTrue();

        Validator.Validate(Valid() with { NetRom = new NetRomConfig { Window = 0 } })
            .IsValid.Should().BeFalse("window must be in 1..127");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { Window = 200 } })
            .IsValid.Should().BeFalse("window must be in 1..127 (8-bit sequence space)");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { TimeToLive = 0 } })
            .IsValid.Should().BeFalse("TTL must be in 1..255");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { TransportRetries = 0 } })
            .IsValid.Should().BeFalse("retries must be positive");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { Enabled = false, Broadcast = true } })
            .IsValid.Should().BeFalse("broadcast requires the service enabled");
        Validator.Validate(Valid() with { NetRom = new NetRomConfig { Enabled = false, Connect = true } })
            .IsValid.Should().BeFalse("connect requires the service enabled");
    }

    [Fact]
    public void Default_netrom_inp3_overlay_validates_disabled()
    {
        // The default-off proof at the validator: a config with no inp3: overrides
        // (Inp3 == NetRomInp3Options.Default ⇒ Enabled == false) validates fine.
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
        Valid().NetRom.Inp3.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Accepts_a_valid_enabled_inp3_overlay()
    {
        var ok = Valid() with
        {
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Connect = true,   // INP3 rides the connected-mode interlink
                Inp3 = new NetRomInp3Options
                {
                    Enabled = true,
                    L3RttInterval = TimeSpan.FromSeconds(60),
                    L3RttResetWindow = TimeSpan.FromSeconds(180),
                    RifInterval = TimeSpan.FromSeconds(300),
                    PositiveDebounce = TimeSpan.FromSeconds(5),
                    SnttGainShift = 3,
                    HopLimit = 30,
                },
            },
        };
        Validator.Validate(ok).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_out_of_range_inp3_values_via_the_records_own_Validate()
    {
        // A simple scalar out of range (SnttGainShift valid 1..8).
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Inp3 = new NetRomInp3Options { SnttGainShift = 0 } },
        }).IsValid.Should().BeFalse("snttGainShift must be in 1..8");

        // The HopLimit floor.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Inp3 = new NetRomInp3Options { HopLimit = 0 } },
        }).IsValid.Should().BeFalse("hopLimit must be at least 1");

        // Cross-field: the reset window must exceed the probe interval.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Inp3 = new NetRomInp3Options
                {
                    L3RttInterval = TimeSpan.FromSeconds(180),
                    L3RttResetWindow = TimeSpan.FromSeconds(60),
                },
            },
        }).IsValid.Should().BeFalse("l3RttResetWindow must exceed l3RttInterval");

        // Cross-field: the positive debounce must be strictly less than the RIF interval.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Inp3 = new NetRomInp3Options
                {
                    RifInterval = TimeSpan.FromSeconds(5),
                    PositiveDebounce = TimeSpan.FromSeconds(5),
                },
            },
        }).IsValid.Should().BeFalse("positiveDebounce must be < rifInterval");
    }

    [Fact]
    public void Inp3_enabled_requires_netrom_enabled()
    {
        // The cross-field guard mirroring broadcast/connect: an overlay on a deaf
        // node is meaningless.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = false, Connect = true, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeFalse("inp3.enabled requires netrom.enabled");

        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Connect = true, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeTrue("inp3.enabled with netrom.enabled + connect is fine");
    }

    [Fact]
    public void Inp3_enabled_requires_netrom_connect()
    {
        // INP3 rides the connected-mode interlink machinery, so the host only constructs the
        // overlay under Connect. Without this guard, inp3.enabled + connect:false would validate
        // and then silently no-op — reject it explicitly (the named-flag discipline).
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Connect = false, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeFalse("inp3.enabled requires netrom.connect");

        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Connect = true, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeTrue("inp3.enabled with netrom.connect is fine");
    }

    [Fact]
    public void Routing_knob_endpoint_or_transit_requires_netrom_enabled()
    {
        // The new single knob is validated through the same resolved-routing gate the
        // legacy connect bool was: a routing role that opens interlinks needs the service on.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = false, Routing = NetRomRouting.Endpoint },
        }).IsValid.Should().BeFalse("routing: endpoint requires netrom.enabled");

        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = false, Routing = NetRomRouting.Transit },
        }).IsValid.Should().BeFalse("routing: transit requires netrom.enabled");

        // routing: none on a disabled node is fine (passive + off — no contradiction).
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = false, Routing = NetRomRouting.None },
        }).IsValid.Should().BeTrue("routing: none on a disabled node is consistent");
    }

    [Fact]
    public void Inp3_enabled_requires_an_interlink_routing_mode_via_the_knob()
    {
        // routing: none ⇒ no interlinks ⇒ INP3 can't ride them ⇒ rejected.
        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Routing = NetRomRouting.None, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeFalse("inp3.enabled requires routing endpoint/transit");

        Validator.Validate(Valid() with
        {
            NetRom = new NetRomConfig { Enabled = true, Routing = NetRomRouting.Endpoint, Inp3 = new NetRomInp3Options { Enabled = true } },
        }).IsValid.Should().BeTrue("inp3.enabled with routing: endpoint is fine");
    }

    [Fact]
    public void Accepts_the_default_traffic_block_and_an_explicit_path()
    {
        Validator.Validate(Valid()).IsValid.Should().BeTrue("the default traffic block (enabled, 14 days, 512 MB) is valid");

        Validator.Validate(Valid() with
        {
            Traffic = new TrafficConfig { Path = "/var/lib/packetnet/traffic.db", RetentionDays = 7, MaxMb = 64 },
        }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 512, null)]      // retention below 1 day
    [InlineData(-1, 512, null)]
    [InlineData(14, 0, null)]       // size cap below 1 MB
    [InlineData(14, -5, null)]
    [InlineData(14, 512, "   ")]    // a set-but-blank path would silently log nowhere
    public void Rejects_out_of_range_traffic_bounds_even_when_disabled(int retentionDays, int maxMb, string? path)
    {
        // Bounds are validated regardless of `enabled` — a disabled-but-edited block
        // can't hold junk that detonates on re-enable.
        var result = Validator.Validate(Valid() with
        {
            Traffic = new TrafficConfig { Enabled = false, Path = path, RetentionDays = retentionDays, MaxMb = maxMb },
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.StartsWith("traffic.", StringComparison.Ordinal));
    }

    // ---- tailscale: (network-access.md S1 — parsed/validated, inert) ----------------

    [Fact]
    public void Tailscale_absent_block_is_valid_and_disabled()
    {
        // The default (no tailscale: key) is an inert, disabled block — fully valid.
        var config = Valid();
        config.Tailscale.Enabled.Should().BeFalse();
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Tailscale_disabled_block_is_unconstrained()
    {
        // When disabled, the shape rules (hostname/target/stateDir) don't apply — the
        // block is inert. (Junk that would be rejected when enabled is tolerated here.)
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig
            {
                Enabled = false,
                Hostname = "Not A Valid Host!",
                Target = "nonsense",
                StateDir = "",
            },
        };
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Tailscale_enabled_default_shape_is_valid()
    {
        var config = Valid() with { Tailscale = new TailscaleConfig { Enabled = true } };
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("pdn", true)]
    [InlineData("pdn-node-1", true)]
    [InlineData("node99", true)]
    [InlineData("", true)]               // empty is allowed — derives <callsign>-pdn
    [InlineData("PDN", false)]           // uppercase rejected by ^[a-z0-9-]+$
    [InlineData("pdn.node", false)]      // a dot is not in the label set
    [InlineData("pdn_node", false)]      // underscore not allowed
    [InlineData("pdn node", false)]      // space not allowed
    public void Tailscale_hostname_must_match_pattern_when_enabled(string hostname, bool expectValid)
    {
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig { Enabled = true, Hostname = hostname },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("127.0.0.1", false)]     // no port
    [InlineData("127.0.0.1:", false)]    // empty port
    [InlineData(":8080", false)]         // empty host
    [InlineData("127.0.0.1:70000", false)] // port out of range
    [InlineData("127.0.0.1:abc", false)] // non-numeric port
    [InlineData("", false)]
    public void Tailscale_target_must_be_host_port_when_enabled(string target, bool expectValid)
    {
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig { Enabled = true, Target = target },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Tailscale_stateDir_required_when_enabled()
    {
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig { Enabled = true, StateDir = "" },
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, true)]                  // neither — interactive login, fine
    [InlineData("tskey-abc", null, true)]           // inline key only
    [InlineData(null, "/etc/packetnet/ts.key", true)] // key file only (preferred)
    [InlineData("tskey-abc", "/etc/packetnet/ts.key", false)] // both — ambiguous
    public void Tailscale_authKey_and_authKeyFile_are_mutually_exclusive(string? authKey, string? authKeyFile, bool expectValid)
    {
        // Enforced ALWAYS (even disabled) — a both-set block is ambiguous regardless.
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig { Enabled = false, AuthKey = authKey, AuthKeyFile = authKeyFile },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Tailscale_funnel_true_with_disabled_is_a_noop_not_an_error()
    {
        // funnel is inert until the sidecar runs; a funnel:true / enabled:false block is
        // a deliberate no-op, kept total rather than rejected.
        var config = Valid() with
        {
            Tailscale = new TailscaleConfig { Enabled = false, Funnel = true },
        };
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    // ─── App packet-identity uniqueness (docs/app-packages.md § Application packet identity) ───

    private static ApplicationConfig InlineApp(string id, string verb, string? callsign = null, AppNetromConfig? netrom = null) =>
        new() { Id = id, Command = verb, Executable = "/bin/cat", Callsign = callsign, Netrom = netrom };

    [Fact]
    public void Two_apps_pinning_the_same_callsign_are_rejected()
    {
        var config = Valid() with
        {
            Applications = [InlineApp("a", "ALPHA", callsign: "M0LTE-3"), InlineApp("b", "BRAVO", callsign: "-3")],
        };
        Validator.Validate(config).IsValid.Should().BeFalse("both resolve to M0LTE-3");
    }

    [Fact]
    public void An_app_pinning_the_nodes_own_callsign_is_rejected()
    {
        var config = Valid() with { Applications = [InlineApp("a", "ALPHA", callsign: "M0LTE-1")] };
        Validator.Validate(config).IsValid.Should().BeFalse("M0LTE-1 is the node's own");
    }

    [Fact]
    public void Distinct_pinned_callsigns_are_accepted()
    {
        var config = Valid() with
        {
            Applications = [InlineApp("a", "ALPHA", callsign: "-3"), InlineApp("b", "BRAVO", callsign: "-4")],
        };
        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_inline_app_and_a_package_override_pinning_the_same_callsign_are_rejected()
    {
        var config = Valid() with
        {
            Applications = [InlineApp("a", "ALPHA", callsign: "M9YYY-2")],
            Apps = [new AppOverrideConfig { Id = "bbs", Enabled = true, Callsign = "M9YYY-2" }],
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Two_apps_advertising_the_same_netrom_alias_are_rejected()
    {
        var config = Valid() with
        {
            Applications =
            [
                InlineApp("a", "ALPHA", netrom: new AppNetromConfig { Alias = "RDGBBS" }),
                InlineApp("b", "BRAVO", netrom: new AppNetromConfig { Alias = "rdgbbs" }),   // case-insensitive clash
            ],
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_app_alias_equal_to_the_nodes_own_alias_is_rejected()
    {
        // The node's own alias is now Identity.Alias (unified); an app may not advertise it.
        var config = Valid() with
        {
            Identity = new Identity { Callsign = "M0LTE-1", Alias = "RDGBBS" },
            Applications = [InlineApp("a", "ALPHA", netrom: new AppNetromConfig { Alias = "RDGBBS" })],
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Two_inline_apps_sharing_a_command_verb_are_rejected()
    {
        var config = Valid() with { Applications = [InlineApp("a", "CHAT"), InlineApp("b", "CHAT")] };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_inline_command_verb_colliding_with_a_built_in_is_rejected()
    {
        var config = Valid() with { Applications = [InlineApp("a", "NODES")] };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    // --- OARC reporting (#459) ---

    [Fact]
    public void Oarc_defaults_are_valid()
    {
        // The default OARC block (disabled, OARC base URL, 300/60s) must validate — a stock node.
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
        Validator.Validate(Valid() with { Oarc = new OarcConfig() }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://node-api.packet.oarc.uk/", true)]
    [InlineData("http://localhost:5000/", true)]      // a local test collector
    [InlineData("", false)]                            // required
    [InlineData("node-api.packet.oarc.uk", false)]     // not absolute
    [InlineData("ftp://node-api.packet.oarc.uk", false)] // not http(s)
    public void Oarc_baseUrl_must_be_an_absolute_http_url(string baseUrl, bool expectValid)
    {
        var config = Valid() with { Oarc = new OarcConfig { BaseUrl = baseUrl } };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Theory]
    [InlineData(300, 60, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 60, false)]    // status interval must be > 0
    [InlineData(300, 0, false)]   // session-status interval must be > 0
    [InlineData(-1, 60, false)]
    public void Oarc_intervals_must_be_positive(int statusSecs, int sessionSecs, bool expectValid)
    {
        var config = Valid() with
        {
            Oarc = new OarcConfig { StatusIntervalSecs = statusSecs, SessionStatusIntervalSecs = sessionSecs },
        };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }

    [Fact]
    public void Oarc_shape_is_validated_even_when_disabled()
    {
        // A disabled-but-edited block must not be allowed to hold junk that would fail the day
        // it is enabled — the URL/interval rules apply unconditionally (cf. RHP/Tailscale).
        var config = Valid() with
        {
            Oarc = new OarcConfig { Enabled = false, BaseUrl = "not-a-url", StatusIntervalSecs = 0 },
        };
        Validator.Validate(config).IsValid.Should().BeFalse();
    }

    // --- Node alias (unified Identity.Alias, the single node-name concept) ---

    [Theory]
    [InlineData(null, true)]       // unset → use the callsign for display + callsign base on the wire
    [InlineData("LONDON", true)]   // 6 chars — the NET/ROM mnemonic shape
    [InlineData("RDG", true)]
    [InlineData("A", true)]
    [InlineData("LONDON1", false)] // 7 — exceeds the 6-octet NET/ROM alias field
    [InlineData("VERYLONGNAME", false)]
    public void Identity_alias_is_capped_at_six_characters(string? alias, bool expectValid)
    {
        var config = Valid() with { Identity = new Identity { Callsign = "M0LTE-1", Alias = alias } };
        Validator.Validate(config).IsValid.Should().Be(expectValid);
    }
}
