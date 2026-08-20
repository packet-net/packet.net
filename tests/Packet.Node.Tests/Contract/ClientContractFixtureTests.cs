using System.Reflection;
using System.Text;
using System.Text.Json;
using Packet.Node.Api;
using Packet.Node.Core.Api;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;
using Packet.NetRom;
using Packet.NetRom.Wire;

namespace Packet.Node.Tests.Contract;

/// <summary>
/// The client/server contract fixtures (review item C018, packet.net#692).
///
/// <para>Nothing crossed the real server's JSON with the SPA's <c>types.ts</c>: there is no
/// OpenAPI export, the UI's own round-trip test only proved its mock was JSON-stable, and drift
/// on either side was invisible until an operator hit it. This test serialises a representative
/// instance of every DTO the control panel consumes -- with the app's REAL wire options
/// (<see cref="NodeConfigJson.ApplyTo"/> over <see cref="JsonSerializerDefaults.Web"/>, exactly
/// as <c>Program.cs</c> configures <c>ConfigureHttpJsonOptions</c>) -- into checked-in JSON under
/// <c>web/packetnet-ui/src/test/contract/</c>, and FAILS when a fixture no longer matches a fresh
/// serialisation.</para>
///
/// <para>The other half of the gate is <c>web/packetnet-ui/src/test/contract.test.ts</c>, which
/// loads these same files, reads <c>types.ts</c> with the TypeScript compiler API, and fails when
/// a wire field is unmodelled, a modelled-required field is absent, a value's kind is wrong, or a
/// closed set has drifted. So a change on EITHER side turns a build red.</para>
///
/// <para>To adopt a deliberate server-side change, re-run with <c>PDN_UPDATE_CONTRACT=1</c> to
/// rewrite the fixtures, then run the vitest side and fix <c>types.ts</c> / <c>mock.ts</c> to
/// match. Never edit a fixture by hand.</para>
/// </summary>
[Trait("Category", "Node")]
public sealed class ClientContractFixtureTests
{
    /// <summary>The app's HTTP wire dialect. Program.cs does exactly this to the HTTP layer's
    /// options; WriteIndented is added only so a fixture diff is readable in review.</summary>
    private static JsonSerializerOptions WireOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        NodeConfigJson.ApplyTo(options);
        return options;
    }

    private static readonly JsonSerializerOptions Wire = WireOptions();

    private static readonly DateTimeOffset At = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Walk up from the test assembly to the repo root (the directory holding the
    /// solution file) -- the same idiom the catalog/packaging tests use.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Packet.NET.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (no Packet.NET.slnx above the test assembly).");
    }

    private static string FixtureDir()
        => Path.Combine(RepoRoot(), "web", "packetnet-ui", "src", "test", "contract");

    private static bool Updating
        => Environment.GetEnvironmentVariable("PDN_UPDATE_CONTRACT") == "1";

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixtures().Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_matches_a_fresh_serialisation_with_the_apps_wire_options(string name)
    {
        var value = Fixtures()[name];
        var fresh = JsonSerializer.Serialize(value, value.GetType(), Wire).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(FixtureDir(), name);

        if (Updating)
        {
            Directory.CreateDirectory(FixtureDir());
            File.WriteAllText(path, fresh, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the contract fixture {name} must be checked in - run with PDN_UPDATE_CONTRACT=1 to generate it");

        var onDisk = File.ReadAllText(path).ReplaceLineEndings("\n");
        onDisk.Should().Be(
            fresh,
            $"the checked-in contract fixture {name} must match what the server serialises today. "
            + "If the server change is deliberate, re-run this test with PDN_UPDATE_CONTRACT=1 and "
            + "then fix web/packetnet-ui/src/lib/types.ts and lib/mock.ts to the new shape.");
    }

    [Fact]
    public void The_fixture_directory_holds_exactly_the_fixtures_this_test_owns()
    {
        // A fixture left behind by a deleted DTO would keep passing the vitest side against a
        // shape the server no longer serves.
        var dir = FixtureDir();
        Directory.Exists(dir).Should().BeTrue();
        var onDisk = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        onDisk.Should().Equal(Fixtures().Keys.Order(StringComparer.Ordinal));
    }

    // ================================================================================
    // The fixtures. Each is a representative instance: every optional field populated,
    // every arm of a union present, so the vitest side sees the full key surface.
    // ================================================================================
    private static Dictionary<string, object> Fixtures() => new(StringComparer.Ordinal)
    {
        ["NodeConfig.json"] = SampleConfig(),
        ["NodeStatus.json"] = new NodeStatus(
            Callsign: "M0LTE-1", Alias: "LONDON", Grid: "IO91nl", Version: "0.40.0 (abc1234)",
            UptimeSeconds: 1_987_260, PortsUp: 3, PortsTotal: 4, SessionCount: 2,
            Netrom: new NetRomSummary(Neighbours: 4, Destinations: 6, Inp3Enabled: true),
            Traffic: new TrafficLogStatus(Enabled: true, Dropped: 0)),
        ["PortStatus.json"] = new[]
        {
            new PortStatus("vhf-1", true, PortStates.Up, 2, null, 184_213, 95_120, [], At, ChannelBusy: false),
            new PortStatus("uhf-2", true, PortStates.Faulted, 0, "serial: /dev/ttyUSB1 not present", 0, 0, [], At, ChannelBusy: null),
            new PortStatus("link-dn", false, PortStates.Disabled, 0, null, 0, 0, [], At, ChannelBusy: true),
            // A serving port with a piece missing - the state the API could not express at all
            // before #722 (it read identical to a healthy port).
            new PortStatus("hf-300", true, PortStates.Degraded, 1, "radio (tait-ccdi on /dev/ttyUSB2): open failed",
                12, 8, [PortComponents.Radio], At, ChannelBusy: null),
        },
        ["SessionInfo.json"] = new[]
        {
            new SessionInfo("vhf-1:M0LTE", "vhf-1", "M0LTE", "GB7RDG-1", "console", "Connected", 12, 11, 4, 842, 4821, 19_233, "0:00:02"),
            new SessionInfo("link-dn:G8PZT-7", "link-dn", "G8PZT-7", "GB7RDG-1", "interlink", "TimerRecovery", 401, 398, 7, 91_244, 2_104_882, 1_988_401, "0:00:00"),
            // The same station, at the same moment, on an APPLICATION callsign: a distinct
            // circuit with a distinct id, which is what the long form of the id exists for (#723).
            new SessionInfo("vhf-1:M0LTE>GB7RDG-4", "vhf-1", "M0LTE", "GB7RDG-4", "console", "Connected", 3, 2, 4, 96, 512, 2_048, "0:00:01"),
        },
        ["LinkStats.json"] = new[]
        {
            new LinkStats("vhf-1", "M0LTE", 612, 0, 1, 0, 1204, 1190),
        },
        ["PeerCapability.json"] = new[]
        {
            new PeerCapability("vhf-1", "M0LTE", true, true, "0:02:14", null),
            new PeerCapability("uhf-2", "G4APL-1", null, null, "5:09:52", "1:41:08"),
        },
        ["MonitorEvent.json"] = SampleFrames(),
        ["HeardStation.json"] = new[]
        {
            new HeardStation("M0LTE", "vhf-1", "2:14:08", "0:00:12", 412, 1,
                LastRssiDbm: -79f, LastSnrDb: 24.5f, MedianPreDataCarrierMs: 210f,
                PreDataCarrierSamples: 12, TxDelayAdvisory: "300 ms is ~90 ms longer than needed"),
            new HeardStation("G8PZT-7", null, "1:03:11", "0:00:03", 20_441, 3),
        },
        ["NetRomRoutingSnapshot.json"] = SampleRoutes(),

        // ---- auth, setup + users ----
        ["UserSummary.json"] = new[]
        {
            UserSummaryOf("tom", "admin", At.AddDays(-200), At.AddMinutes(-3), hasTotp: true, callsign: "M0LTE"),
            UserSummaryOf("guest", "read", At.AddDays(-2), null, hasTotp: false, callsign: null),
        },
        ["LoginResult.json"] = new PdnAuthApi.LoginResponse(
            Token: "eyJhbGciOiJIUzI1NiJ9.e30.sig", ExpiresAt: At.AddHours(1),
            Scopes: "admin", RefreshToken: "rt-0123456789abcdef", Username: "tom"),
        ["SetupState.json"] = new PdnAuthApi.SetupStateResponse(NeedsSetup: false),
        ["SetupResult.json"] = new PdnAuthApi.SetupResponse(Username: "admin", Scope: "admin"),
        // The wizard's device picker (GET /setup/devices). Both rows matter: an identified
        // NinoTNC bound to its stable by-id path, and an unidentified port that carries the
        // reason it could not be identified.
        ["ModemScan.json"] = new ModemScan(
        [
            new ModemScanDevice(
                "/dev/serial/by-id/usb-Microchip_Technology_Inc._NinoTNC-if00", "/dev/ttyACM0",
                "usb-Microchip_Technology_Inc._NinoTNC-if00", "nino-tnc", "3.44", null, null),
            new ModemScanDevice("/dev/ttyUSB0", "/dev/ttyUSB0", null, "serial", null, null, "no reply"),
        ], PermissionDenied: false),
        ["WebAuthnCredential.json"] = new[]
        {
            new WebAuthnCredentialSummary("Y3JlZC1pZA", "internal,hybrid", At.AddDays(-30), At.AddHours(-2)),
            new WebAuthnCredentialSummary("b3RoZXItaWQ", null, At.AddDays(-9), null),
        },
        ["RegisterCompleteResponse.json"] = new PdnWebAuthnApi.RegisterCompleteResponse(Registered: true, CredentialId: "Y3JlZC1pZA"),
        ["TotpEnrollState.json"] = new PdnTotpApi.EnrollStateResponse(Enrolled: true, Callsign: "M0LTE"),
        ["TotpEnrollBeginResponse.json"] = new PdnTotpApi.EnrollBeginResponse(
            Secret: "JBSWY3DPEHPK3PXP", OtpauthUri: "otpauth://totp/pdn:tom?secret=JBSWY3DPEHPK3PXP&issuer=pdn"),
        ["TotpEnrollCompleteResponse.json"] = new PdnTotpApi.EnrollCompleteResponse(Enrolled: true, Callsign: "M0LTE"),

        // ---- radios, rigs, head-ends, diagnostics ----
        ["RadioStatus.json"] = new[]
        {
            new RadioStatus("vhf-1", true, "tait-ccdi", "/dev/ttyUSB0", "19925328",
                new RadioIdentity("Tait TM8110", "1.10.0"), "healthy", true,
                new RadioHealth(-78.5f, -80.2f, 41, 2140, 190, 0.089, At.AddSeconds(-4))),
            new RadioStatus("hf-300", false, "rig", null, null, null, "unknown", null, null),
        },
        ["RadioScanResult.json"] = new[]
        {
            new RadioScanResult("19925328", "Tait TM8110", "1.10.0", 28_800, "/dev/ttyUSB0",
                "/dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0", BandCode: "B1", AmateurBand: "2m"),
            new RadioScanResult("1G000123", "Tait TM8110", "1.10.0", 28_800, "/dev/ttyUSB2", null),
        },
        ["RigStatus.json"] = new[]
        {
            new RigStatus("hf-300", true, "hamlib", "127.0.0.1:4532", "Hamlib rigctld", "Icom", "IC-7300",
                ["frequencyGet", "frequencySet", "modeGet", "modeSet", "pttGet", "swrMeter"],
                "healthy", 14_074_000, "PKTUSB", 3000, false,
                new RigMeters(1.3, 42, 0.42, At.AddSeconds(-90)), At.AddSeconds(-3)),
            new RigStatus("vhf-1", false, "flrig", "127.0.0.1:12345", null, null, null, [],
                "unknown", null, null, null, null, null, null),
        },
        ["RigScan.json"] = new RigScan(
        [
            new RigScanDevice("/dev/ttyUSB3", "/dev/serial/by-id/usb-Icom_Inc._IC-7300-if00-port0",
                "usb-Icom_Inc._IC-7300-if00-port0", null, new RigSuggestion("Icom", "IC-7300", 3073, "by-id")),
            new RigScanDevice("/dev/ttyUSB1", null, null, "port 'hf-300' transport (serial-kiss)", null),
        ], CatalogueAvailable: true),
        // GET /rigs/models answers an anonymous { available, models } (PdnRigsApi.cs), so the
        // shape is mirrored here rather than serialised from a named DTO. The rows themselves
        // ARE the server's RigCatalogueModel, so a change to those is still caught.
        ["RigModelCatalogue.json"] = new
        {
            available = true,
            models = new[]
            {
                new RigCatalogueModel(1, "Hamlib", "Dummy", "Stable"),
                new RigCatalogueModel(3073, "Icom", "IC-7300", null),
            },
        },
        ["SoundModemQualitySnapshot.json"] = new SoundModemQualitySnapshot(
            Frames: 1842, CumulativeCorrectedBytes: 271, FramesWithCorrections: 63,
            LastFrameCorrectedBytes: 2,
            Recent:
            [
                new SoundModemFrameQuality(At.AddSeconds(-1), "qpsk2400-il2pc", 128, 2, true, 12, 3),
                new SoundModemFrameQuality(At.AddSeconds(-9), "afsk1200", 47, null, null, null, null),
            ]),
        ["DoctorReport.json"] = new PortDoctorReport("vhf-1",
        [
            new PortDoctorProbe("tnc-present", "pass", "GETVER answered: firmware 3.44", null),
            new PortDoctorProbe("dip-software-control", "fail", "DIPs 0110 - mode pinned by switches",
                "set all four DIP switches up (1111)"),
            new PortDoctorProbe("sdm", "unknown", "requires a brief transmit", null),
        ], At),
        ["HeadEndScan.json"] = SampleHeadEnds(),
        ["HeadEndKeyupResult.json"] = new HeadEndKeyupResult(
            "garage-pi", Reachable: true, Error: null,
            Pairs: [new HeadEndKeyupPair("acm-0", "usb-1")],
            UnpairedTncs: ["ttyACM1"], UnpairedRadios: [],
            Ambiguous: [new HeadEndKeyupAmbiguity("ttyACM1", ["usb-0", "usb-1"])],
            Caveat: HeadEndKeyupCaveat.Text),
        ["PingResult.json"] = new PingResult(
        [
            new PingReply(1, 612, false),
            new PingReply(2, null, true),
        ], MinMs: 612, AvgMs: 612, MaxMs: 612, LossPct: 50),
        ["TuningSessionInfo.json"] = new TuningSessionInfo(
            "tune-1", "vhf-1", "tuned", "12345678", "armed", 5, At),
        ["TuningEvent.json"] = new[]
        {
            new TuningEvent("armed", At, "armed"),
            new TuningEvent("round", At.AddSeconds(2), "peer-connected", BurstIndex: 1, Decoded: 4, Total: 5,
                LevelDb: -55.0, RssiDbm: -90.3, Advice: "up", Note: "turn the deviation up", Error: null,
                TxDelayMs: 300, PreDataCarrierMs: 212.5, RecommendedTxDelayMs: 220),
            new TuningEvent("error", At.AddSeconds(4), "error", Error: "peer stopped answering"),
        },

        // ---- apps ----
        ["NodeApp.json"] = new[]
        {
            new PdnAppGateway.AppTile("wall", "WALL", "message-square", "/apps/wall/", "standalone", "Running"),
            new PdnAppGateway.AppTile("lobby", "LOBBY", null, "/apps/lobby/", "slot", null),
        },
        ["AppPackage.json"] = new[]
        {
            new PdnAppPackagesApi.AppPackageEntry("wall", "WALL", "1.2.0", "Shared message wall",
                "message-square", ["session", "web"], Enabled: true, Source: "package",
                Installed: true, Error: null,
                Service: "managed", State: "Running", Pid: 4711, Detail: null,
                Forwards: [new PdnAppPackagesApi.AppForwardEntry(993, "127.0.0.1:1430", "terminate")],
                Command: "WALL", Callsign: "M0LTE-1", PinnedCallsign: "M0LTE-1",
                NetromAlias: "RDGWAL", NetromQuality: 255),
            new PdnAppPackagesApi.AppPackageEntry("wx", "wx", null, null, null, [], Enabled: false,
                Source: "inline", Installed: true, Error: "pdn-app.yaml: missing required field 'command'",
                Service: "none", State: null, Pid: null, Detail: null, Forwards: [],
                Command: null, Callsign: null, PinnedCallsign: null,
                NetromAlias: null, NetromQuality: null),
            // The configured-but-not-installed row (#738 item 2): an apps: override naming a
            // package no root holds. No manifest, so no name/version/capabilities - the client
            // has to render it from `installed: false` alone.
            new PdnAppPackagesApi.AppPackageEntry("ghost", "ghost", null, null, null, [],
                Enabled: true, Source: "package", Installed: false, Error: null,
                Service: "none", State: null, Pid: null, Detail: null, Forwards: [],
                Command: null, Callsign: null, PinnedCallsign: null,
                NetromAlias: null, NetromQuality: null),
        },
        ["AvailableApp.json"] = new[]
        {
            new PdnAvailableAppsApi.AvailableApp("dapps", "DAPPS", "0.34.1", "Store-and-forward messaging",
                "inbox", ["network", "web"], "https://github.com/packet-net/dapps", "assets",
                Installed: false, InstalledVersion: null, UpdateAvailable: false, Installable: true),
            new PdnAvailableAppsApi.AvailableApp("bpqchat", "BPQ Chat", "0.1.0", null, null, [], null, "deb",
                Installed: true, InstalledVersion: "0.0.9", UpdateAvailable: true, Installable: false),
        },
        // POST /apps/available/{id}/install answers an anonymous body (PdnAvailableAppsApi.cs);
        // mirrored here. `restarted` is the field the client did not model.
        ["InstallOutcome.json"] = new { ok = true, id = "dapps", version = "0.34.1", restarted = false },

        // ---- system ----
        ["SystemInfo.json"] = new SystemInfoResponse("0.40.0", "github", "github", true, "0.41.0"),
        ["TailscaleStatus.json"] = new TailscaleStatusResponse(true, "running", "pdn.tail-scale.ts.net", null, false),

        // ---- config write ----
        ["ReconcileResult.json"] = new ReconcileResult(
            Valid: true,
            Live: [new ReconcileChange("netRom.minQuality", "live", "40 -> 60")],
            PortRestart: [new ReconcileChange("ports.vhf-1.kiss.txDelay", "port-restart", "30 -> 40")],
            NodeReset: [new ReconcileChange("identity.callsign", "node-reset", "M0LTE-1 -> M0LTE-2")],
            Applied: true),
        ["ValidationProblem.json"] = new ValidationProblem(
        [
            new ConfigValidationError("Ports[0].Profile", "Port.Profile 'vhf-fm-1200' is not a known channel profile"),
        ]),

        // ---- the closed sets ----
        ["closed-sets.json"] = ClosedSets(),
    };

    // ---- builders --------------------------------------------------------------

    private static UserSummary UserSummaryOf(
        string username, string scope, DateTimeOffset created, DateTimeOffset? lastLogin,
        bool hasTotp, string? callsign)
        => new(username, scope, created, lastLogin, hasTotp, callsign);

    /// <summary>A NodeConfig carrying EVERY transport kind, a rig, a head-end-bound radio and a
    /// populated netRom/inp3 block, so the fixture exposes the whole config key surface.</summary>
    private static NodeConfig SampleConfig() => new()
    {
        SchemaVersion = NodeConfig.CurrentSchemaVersion,
        Identity = new Identity { Callsign = "M0LTE-1", Alias = "LONDON", Grid = "IO91nl" },
        Ports =
        [
            new PortConfig
            {
                Id = "vhf-1",
                Enabled = true,
                Transport = new NinoTncTransport { Device = "/dev/ttyACM0", Baud = 57_600, Mode = 4 },
                Profile = "slow-afsk1200",
                Ax25 = new Ax25PortParams { T1Ms = 3000, T2Ms = 300, T3Ms = 180_000, N2 = 8, WindowSize = 4, N1 = 256, MaxCachedPeers = 64 },
                Kiss = new KissParams { TxDelay = 30, Persistence = 63, SlotTime = 10, TxTail = 5, AckMode = true, T1FromTxComplete = true },
                Radio = new PortRadioConfig { Kind = "tait-ccdi", Serial = "19925328", Baud = 28_800, HealthIntervalSeconds = 10, HailResponder = true, HailResponderPeer = "M0LTE-2" },
                Rig = new PortRigConfig { Kind = "hamlib", Device = "/dev/serial/by-id/usb-Icom_Inc._IC-7300-if00-port0", Model = 3073, SerialSpeed = 115_200, PollIntervalSeconds = 5, MeterIntervalSeconds = 1 },
                Compat = new PortCompatConfig { Preset = "lenient", AllowEmptyCallsignBase = true, AllowInfoOnSupervisoryFrames = false, AllowCommandFrameAsResponse = null, Quirks = "default" },
                Link = new PortLinkConfig { Dial = LinkDialPreference.V20, PreConnectXid = LinkPreConnectXid.On },
                Beacon = new PortBeaconConfig { Enabled = true, IntervalMinutes = 15, Text = "{node}:{call} 2m" },
                NetRomQuality = 192,
                NetRomMinQuality = 100,
                NodesPaclen = 160,
                MqttInstance = "2m",
            },
            new PortConfig { Id = "uhf-2", Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 } },
            new PortConfig { Id = "hf-300", Enabled = false, Transport = new SerialKissTransport { Device = "/dev/ttyUSB1", Baud = 38_400 } },
            new PortConfig
            {
                Id = "2m-headend",
                Transport = new NinoTncTcpTransport { HeadEndId = "shack-pi", DeviceId = "nino-0", Mode = 4 },
                Radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "shack-pi", DeviceId = "tait-0" },
            },
            new PortConfig { Id = "link-dn", Transport = new AxudpTransport { Host = "44.131.91.2", Port = 10_093, LocalPort = 10_093 } },
            new PortConfig
            {
                Id = "mp-net",
                Transport = new AxudpMultipointTransport
                {
                    LocalPort = 10_094,
                    Peers =
                    [
                        new AxudpPeerConfig { Call = "N0CALL-1", Host = "44.131.10.1", Port = 10_093, Broadcast = true },
                        new AxudpPeerConfig { Call = "N0CALL-7", Host = "44.131.10.2", Port = 10_094, Broadcast = false },
                    ],
                },
            },
            new PortConfig
            {
                Id = "tait-tp",
                Transport = new TaitTransparentTransportConfig
                {
                    Device = "/dev/ttyUSB3", Serial = "", HeadEndId = "", DeviceId = "",
                    Baud = 28_800, TransparentBaud = 28_800, FfskBaud = 2400, LeadInMs = 100,
                },
            },
            new PortConfig
            {
                Id = "sm-1",
                Transport = new SoundModemTransportConfig
                {
                    Device = "flex:0", CaptureRate = 48_000, Mode = "qpsk2400-il2pc", Frequency = 1700,
                    OffsetPairs = 2, OffsetStepHz = 12.5, PskDetector = "coherent", Ptt = "",
                    Flex = new SoundModemFlexConfig { Frequency = "14.100000", Antenna = "ANT1", Mode = "DIGU", DaxChannel = "1" },
                },
            },
        ],
        Services = new ServicesConfig { Banner = "Welcome to {node} ({call})", Prompt = "{call}> " },
        Management = new ManagementConfig
        {
            Telnet = new TelnetConfig { Enabled = true, Bind = "127.0.0.1", Port = 8011 },
            Http = new HttpConfig { Bind = "0.0.0.0", Port = 8080 },
            Https = new HttpsConfig { Enabled = false, Bind = "0.0.0.0", Port = 8443, CertificatePath = null, CertificatePassword = null, GenerateSelfSignedOnMissing = true },
            Auth = new AuthConfig
            {
                Enabled = true, AccessTokenMinutes = 60, RefreshTokenMinutes = 43_200, SysopElevationMinutes = 30,
                WebAuthn = new WebAuthnConfig { RelyingPartyId = "pdn.m0lte.uk", RelyingPartyName = "pdn node", AllowedOrigins = ["https://pdn.m0lte.uk"] },
            },
            Mdns = new MdnsConfig { Enabled = true, InstanceName = "london" },
            Console = new SysopConsoleConfig { IdleTimeoutMinutes = 30 },
        },
        NetRom = new NetRomConfig
        {
            Enabled = true, Broadcast = true, Routing = NetRomRouting.Transit, Connect = null, Forward = null,
            ForwardMode = NetRomForwardMode.PerFlow,
            DefaultNeighbourQuality = 192, MinQuality = 40,
            ObsoleteInitial = 6, ObsoleteMinimum = 4, SweepIntervalSeconds = 300,
            Window = 4, TransportTimeoutSeconds = 60, TransportRetries = 3, TimeToLive = 25,
            Compress = false,
            Inp3 = NetRomInp3Options.Default with { Enabled = true, PreferInp3Routes = true },
        },
        Beacon = new BeaconConfig { Enabled = true, IntervalMinutes = 30, Text = "{node} pdn node" },
        Tailscale = new TailscaleConfig { Enabled = false, AuthKey = null, AuthKeyFile = null, Hostname = "pdn", Tags = ["tag:pdn"], StateDir = "/var/lib/packetnet/tsnet", Target = "127.0.0.1:8080", Funnel = false },
        Oarc = new OarcConfig { Enabled = false },
        Ardop = new ArdopConfig { Enabled = false, Flex = null },
        Paging = new PagingConfig { Enabled = false, Flex = null },
        HeadEnds = [new HeadEndConfig { Id = "shack-pi", Address = "192.168.1.44:8080" }],
        Apps = [new AppOverrideConfig { Id = "wall", Enabled = true, Command = "WALL", Callsign = "M0LTE-3", Netrom = new AppNetromConfig { Alias = "RDGWAL", Quality = 255 } }],
        AppPackageRoots = ["/var/lib/packetnet/apps"],
    };

    private static MonitorEvent[] SampleFrames() =>
    [
        // An I-frame with radio metadata: the whole additive block populated.
        new MonitorEvent(1, At, "vhf-1", "in", "M0LTE", "GB7RDG", "I", "I", "0xF0", "No layer 3",
            Ns: 3, Nr: 5, Pf: 1, Command: true, Length: 37, Summary: "I N(S)=3 N(R)=5 P=1 pid=0xF0 len=20",
            Raw: [0x96, 0x8E, 0x6E], Path: ["GB7BNS"])
        {
            Control = 0x76, InfoLength = 20, RssiDbm = -78.5f, SnrDb = 24.0f, NoiseFloorDbm = -102.5f,
            BootId = "0123456789abcdef",
        },
        // A supervisory frame, no PID, no radio metadata.
        new MonitorEvent(2, At.AddSeconds(1), "vhf-1", "out", "GB7RDG", "M0LTE", "RR", "S", null, null,
            Ns: null, Nr: 6, Pf: 0, Command: false, Length: 15, Summary: "RR N(R)=6",
            Raw: [0x96], Path: []) { Control = 0xC1, InfoLength = 0, BootId = "0123456789abcdef" },
        // The TEST U-frame POST /ping transmits.
        new MonitorEvent(3, At.AddSeconds(2), "vhf-1", "out", "GB7RDG", "M0LTE", "TEST", "U", null, null,
            Ns: null, Nr: null, Pf: 1, Command: true, Length: 21, Summary: "TEST (loopback)",
            Raw: [0x96], Path: []) { Control = 0xF3, InfoLength = 6, BootId = "0123456789abcdef" },
    ];

    /// <summary>Mirrors PdnReadApi.BuildNetRomRoutes: the endpoint hand-builds an anonymous shape
    /// (Callsign is a struct that must be stringified, and the instants render relative-ago), so
    /// there is no named DTO to serialise. The live-smoke job drives the real endpoint.</summary>
    private static object SampleRoutes() => new
    {
        generatedAt = At,
        // GB7BNS is DUAL-HOMED: audible on vhf-1 and uhf-2, so it is two neighbour rows with
        // their own path qualities (a neighbour is keyed (port, callsign) - #725). A client that
        // keys neighbour rows on the callsign alone renders duplicates, so the fixture carries
        // the case.
        neighbours = new[]
        {
            new { neighbour = "GB7BNS", alias = "BNSGW", portId = "vhf-1", pathQuality = 203, lastHeard = "0:00:14" },
            new { neighbour = "GB7BNS", alias = "BNSGW", portId = "uhf-2", pathQuality = 168, lastHeard = "0:00:31" },
        },
        destinations = new[]
        {
            new
            {
                destination = "GB7CIP",
                alias = "CIPGW",
                bestRoute = 0,
                routes = new[]
                {
                    new { neighbour = "GB7CIP", portId = "vhf-1", quality = 188, obsolescence = 6, inp3 = (object?)new { targetTimeMs = 410, hopCount = 1 } },
                    // The same next-hop CALLSIGN on two ports: two distinct routes, each with the
                    // quality its own port's QUALITY derived.
                    new { neighbour = "GB7BNS", portId = "vhf-1", quality = 142, obsolescence = 4, inp3 = (object?)null },
                    new { neighbour = "GB7BNS", portId = "uhf-2", quality = 118, obsolescence = 4, inp3 = (object?)null },
                },
            },
        },
    };

    private static HeadEndScan SampleHeadEnds() => new(
    [
        new HeadEndInstanceScan(
            InstanceId: "shack-pi", Host: "192.168.1.44", HttpPort: 8080, Source: "mdns",
            Reachable: true, Error: null,
            Devices:
            [
                new HeadEndDeviceScan("nino-0", HeadEndDeviceKind.NinoTnc, "NinoTNC N9600A4", "3.44", null, 57_600, true,
                    BandCode: null, AmateurBand: null, IdSource: "by-path", IdStable: true),
                new HeadEndDeviceScan("tait-0", HeadEndDeviceKind.TaitCcdi, "Tait TM8110", "1.10.0", "19925328", 28_800, true,
                    BandCode: "B1", AmateurBand: "2m", IdSource: "dev", IdStable: false),
                new HeadEndDeviceScan("unknown-0", HeadEndDeviceKind.Unknown, null, null, null, 9600, false),
            ],
            ProposedPairs: [new HeadEndPairProposal("nino-0", "tait-0", true)],
            PairingAmbiguous: false,
            ReachableNow: true,
            LastSeen: At.AddSeconds(-30)),
        new HeadEndInstanceScan("attic-relay", "192.168.1.77", 8080, "config", false,
            "connection refused", [], [], false),
    ],
    [new HeadEndConflict("spare-pi", ["192.168.1.90:8080", "192.168.1.91:8080"])]);

    /// <summary>Every closed set the SPA mirrors as a TypeScript union. Each is READ FROM the
    /// server's own source of truth (a const table, an enum, or the frame classifier swept over
    /// all 256 control octets), so adding an arm on the server fails this fixture and then the
    /// vitest that compares it to types.ts.</summary>
    private static object ClosedSets() => new
    {
        channelProfiles = ChannelProfiles.Names,
        transportKinds = ConstStringsOf(typeof(TransportKinds)),
        authScopes = ConstStringsOf(typeof(AuthScopes)).Where(s => s is "read" or "operate" or "admin").ToArray(),
        radioKinds = RadioKinds.Names,
        rigKinds = RigKinds.Names,
        compatPresets = Ax25CompatPresets.PresetNames,
        compatQuirks = Ax25CompatPresets.QuirksNames,
        linkDial = Enum.GetNames<LinkDialPreference>(),
        linkPreConnectXid = Enum.GetNames<LinkPreConnectXid>(),
        headEndDeviceKinds = ConstStringsOf(typeof(HeadEndDeviceKind)),
        appPackageStates = Enum.GetNames<AppServiceState>(),
        portStates = PortStates.All,
        netRomRouting = Enum.GetNames<NetRomRouting>(),
        netRomForwardMode = Enum.GetNames<NetRomForwardMode>(),
        appUiModes = Enum.GetNames<AppUiMode>().Select(n => n.ToLowerInvariant()).ToArray(),
        frameTypes = FrameTypes(),
        frameClasses = FrameClasses(),
    };

    /// <summary>Every public const string on a static class, in declaration order.</summary>
    private static string[] ConstStringsOf(Type t) =>
        [.. t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    /// <summary>Sweep every control octet through the monitor's own classifier: the exact set of
    /// `type` values the SSE feed can carry, derived rather than transcribed.</summary>
    private static string[] FrameTypes() =>
        [.. Enumerable.Range(0, 256)
            .Select(c => MonitorEventFactory.Classify((byte)c).Type)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static string[] FrameClasses() =>
        [.. Enumerable.Range(0, 256)
            .Select(c => MonitorEventFactory.Classify((byte)c).ClassKind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
