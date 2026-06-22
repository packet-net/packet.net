using FsCheck;
using FsCheck.Fluent;   // brings the Gen Select/SelectMany LINQ extension methods into scope
using Gen = FsCheck.Fluent.Gen;
using Arb = FsCheck.Fluent.Arb;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// FsCheck generators producing <em>valid</em> <see cref="NodeConfig"/> values —
/// the substrate for the serialise↔parse round-trip property. "Valid" here means
/// it passes <see cref="NodeConfigValidator"/>: parseable callsign, unique port
/// ids + endpoints, in-range params. Generating only valid configs keeps the
/// round-trip property about the codec, not about validation.
/// </summary>
public static class NodeConfigArbitraries
{
    private static Gen<string> CallsignGen()
    {
        // 1–6 uppercase alnum base + optional SSID 0–15 — exactly what Callsign
        // accepts, so every generated identity validates.
        var letter = Gen.Elements("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray());
        return
            from len in Gen.Choose(1, 6)
            from chars in Gen.ListOf(letter, len)
            from ssid in Gen.Choose(0, 15)
            let baseCall = new string(chars.ToArray())
            select ssid == 0 ? baseCall : $"{baseCall}-{ssid}";
    }

    private static Gen<TransportConfig> TransportGen(int index)
    {
        // Make the endpoint unique per port index so the no-two-ports-on-one-
        // device validation rule always holds for a generated config.
        var serial = from baud in Gen.Choose(1, 921600)
                     select (TransportConfig)new SerialKissTransport { Device = $"/dev/tty{index}", Baud = baud };
        var nino = from baud in Gen.Choose(1, 921600)
                   from mode in Gen.Choose(0, 15)
                   select (TransportConfig)new NinoTncTransport { Device = $"/dev/nino{index}", Baud = baud, Mode = mode };
        var tcp = from port in Gen.Choose(1, 65535)
                  select (TransportConfig)new KissTcpTransport { Host = $"host{index}.local", Port = port };
        return Gen.OneOf(serial, nino, tcp);
    }

    private static Gen<Ax25PortParams?> Ax25Gen() =>
        Gen.OneOf(
            Gen.Constant<Ax25PortParams?>(null),
            from t1 in Gen.Choose(1, 60000)
            from n2 in Gen.Choose(1, 20)
            from k in Gen.Choose(1, 127)
            select (Ax25PortParams?)new Ax25PortParams { T1Ms = t1, N2 = n2, WindowSize = k });

    private static Gen<KissParams?> KissGen() =>
        Gen.OneOf(
            Gen.Constant<KissParams?>(null),
            from txd in Gen.Choose(0, 255)
            from per in Gen.Choose(0, 255)
            select (KissParams?)new KissParams { TxDelay = (byte)txd, Persistence = (byte)per });

    private static Gen<PortCompatConfig?> CompatGen() =>
        Gen.OneOf(
            Gen.Constant<PortCompatConfig?>(null),
            from preset in Gen.Elements(null, "strict", "lenient", "bpq", "xrouter", "direwolf")
            from quirks in Gen.Elements(null, "default", "strictly-faithful")
            from cmdAsResp in Gen.Elements<bool?>(null, true, false)
            from emptyBase in Gen.Elements<bool?>(null, true, false)
            select (PortCompatConfig?)new PortCompatConfig
            {
                Preset = preset,
                Quirks = quirks,
                AllowCommandFrameAsResponse = cmdAsResp,
                AllowEmptyCallsignBase = emptyBase,
            });

    private static Gen<PortConfig> PortGen(int index) =>
        from enabled in Gen.Elements(false, true)
        from transport in TransportGen(index)
        from ax25 in Ax25Gen()
        from kiss in KissGen()
        from compat in CompatGen()
        select new PortConfig
        {
            Id = $"port{index}",
            Enabled = enabled,
            Transport = transport,
            Ax25 = ax25,
            Kiss = kiss,
            Compat = compat,
        };

    private static Gen<NetRomConfig> NetRomGen() =>
        Gen.OneOf(
            Gen.Constant(new NetRomConfig()),
            // Enabled with an explicit routing knob (endpoint/transit need enabled, which
            // this branch guarantees) — exercises the new knob's serialise↔parse round-trip.
            from routing in Gen.Elements(NetRomRouting.None, NetRomRouting.Endpoint, NetRomRouting.Transit)
            from defQ in Gen.Choose(0, 255)
            from minQ in Gen.Choose(0, 255)
            from obs in Gen.Choose(1, 12)
            from sweep in Gen.Choose(1, 7200)
            select new NetRomConfig
            {
                Enabled = true,
                Routing = routing,
                DefaultNeighbourQuality = defQ,
                MinQuality = minQ,
                ObsoleteInitial = obs,
                SweepIntervalSeconds = sweep,
            },
            // A disabled, passive node (no routing knob) — the other valid axis.
            from defQ in Gen.Choose(0, 255)
            from minQ in Gen.Choose(0, 255)
            select new NetRomConfig
            {
                Enabled = false,
                DefaultNeighbourQuality = defQ,
                MinQuality = minQ,
            });

    private static Gen<TrafficConfig> TrafficGen() =>
        Gen.OneOf(
            Gen.Constant(new TrafficConfig()),
            from enabled in Gen.Elements(false, true)
            from path in Gen.Elements<string?>(null, "traffic.db", "/var/lib/packetnet/traffic.db")
            from days in Gen.Choose(1, 60)
            from maxMb in Gen.Choose(1, 4096)
            select new TrafficConfig { Enabled = enabled, Path = path, RetentionDays = days, MaxMb = maxMb });

    private static Gen<TailscaleConfig> TailscaleGen() =>
        Gen.OneOf(
            Gen.Constant(new TailscaleConfig()),
            // Only VALID blocks (the round-trip property generates configs that must pass
            // the validator): a legal ^[a-z0-9-]+$ hostname, a host:port target, a
            // non-empty stateDir, and at most one of authKey/authKeyFile.
            from enabled in Gen.Elements(false, true)
            from hostname in Gen.Elements("pdn", "rdg-pdn", "node1")
            from tags in Gen.Elements<IReadOnlyList<string>>([], ["tag:server"], ["tag:server", "tag:packetnet"])
            from key in Gen.Elements<(string?, string?)>((null, null), ("tskey-abc", null), (null, "/etc/packetnet/ts.key"))
            from funnel in Gen.Elements(false, true)
            select new TailscaleConfig
            {
                Enabled = enabled,
                Hostname = hostname,
                Tags = tags,
                AuthKey = key.Item1,
                AuthKeyFile = key.Item2,
                StateDir = "/var/lib/packetnet/tsnet",
                Target = "127.0.0.1:8080",
                Funnel = funnel,
            });

    private static Gen<OarcConfig> OarcGen() =>
        Gen.OneOf(
            Gen.Constant(new OarcConfig()),
            // Only VALID blocks (the round-trip property generates configs that must pass the
            // validator): an absolute http(s) baseUrl and strictly-positive intervals.
            from enabled in Gen.Elements(false, true)
            from baseUrl in Gen.Elements("https://node-api.packet.oarc.uk/", "http://localhost:5000/", "https://staging.example/")
            from nodeStatus in Gen.Elements(false, true)
            from links in Gen.Elements(false, true)
            from circuits in Gen.Elements(false, true)
            from traces in Gen.Elements(false, true)
            from rfOnly in Gen.Elements(false, true)
            from exactPos in Gen.Elements(false, true)
            from statusSecs in Gen.Choose(1, 3600)
            from sessionSecs in Gen.Choose(1, 3600)
            select new OarcConfig
            {
                Enabled = enabled,
                BaseUrl = baseUrl,
                ReportNodeStatus = nodeStatus,
                ReportLinks = links,
                ReportCircuits = circuits,
                ReportTraces = traces,
                TracesRfOnly = rfOnly,
                PublishExactPosition = exactPos,
                StatusIntervalSecs = statusSecs,
                SessionStatusIntervalSecs = sessionSecs,
            });

    private static Gen<MdnsConfig> MdnsGen() =>
        Gen.OneOf(
            Gen.Constant(new MdnsConfig()),
            // Only VALID names (the round-trip generates configs that must pass the validator):
            // no leading '-', no control chars, ≤63 bytes; a space is fine for a DNS-SD label.
            from enabled in Gen.Elements(false, true)
            from instance in Gen.Elements<string?>(null, "Hilltop", "RDG node", "M0LTE-7")
            select new MdnsConfig { Enabled = enabled, InstanceName = instance });

    // Vary only Mdns; the other management sub-configs keep their valid defaults, so the
    // round-trip exercises the new mdns block without re-deriving all of ManagementConfig.
    private static Gen<ManagementConfig> ManagementGen() =>
        from mdns in MdnsGen()
        select new ManagementConfig { Mdns = mdns };

    // A valid inline application: unique id + a launch verb that isn't a built-in console
    // verb (APPV<n> never collides), a process executable, and small Args/Capabilities lists.
    // The collection members are what exercise ApplicationConfig's hand-rolled value equality
    // through the serialise→parse round-trip (the bug class the equality override fixes).
    private static Gen<ApplicationConfig> ApplicationGen(int index) =>
        from enabled in Gen.Elements(true, false)
        from args in Gen.Elements<IReadOnlyList<string>>([], ["run"], ["run", "--port=1"])
        from caps in Gen.Elements<IReadOnlyList<string>>([], ["session"], ["session", "network"])
        select new ApplicationConfig
        {
            Id = $"app{index}",
            Command = $"APPV{index}",
            Enabled = enabled,
            Kind = ApplicationKind.Process,
            Executable = "/usr/bin/app",
            Args = args,
            Capabilities = caps,
        };

    // A valid app-package override: only the id is constrained; the Environment dict is the
    // collection member that exercises AppOverrideConfig's value equality across the round-trip.
    private static Gen<AppOverrideConfig> AppOverrideGen(int index) =>
        from enabled in Gen.Elements(true, false)
        from env in Gen.Elements<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["K"] = "V" },
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" })
        select new AppOverrideConfig
        {
            Id = $"pkg{index}",
            Enabled = enabled,
            Environment = env,
        };

    private static Gen<NodeConfig> NodeConfigGen() =>
        from call in CallsignGen()
        from nPorts in Gen.Choose(0, 4)
        from ports in Gen.CollectToList(Enumerable.Range(0, nPorts).Select(PortGen))
        from nApps in Gen.Choose(0, 2)
        from apps in Gen.CollectToList(Enumerable.Range(0, nApps).Select(ApplicationGen))
        from nOverrides in Gen.Choose(0, 2)
        from overrides in Gen.CollectToList(Enumerable.Range(0, nOverrides).Select(AppOverrideGen))
        from netrom in NetRomGen()
        from traffic in TrafficGen()
        from tailscale in TailscaleGen()
        from oarc in OarcGen()
        from management in ManagementGen()
        select new NodeConfig
        {
            SchemaVersion = Packet.Node.Core.Configuration.NodeConfig.CurrentSchemaVersion,
            Identity = new Identity { Callsign = call },
            Ports = ports.ToList(),
            Applications = apps.ToList(),
            Apps = overrides.ToList(),
            NetRom = netrom,
            Traffic = traffic,
            Tailscale = tailscale,
            Oarc = oarc,
            Management = management,
        };

    public static Arbitrary<NodeConfig> NodeConfig() => Arb.From(NodeConfigGen());
}
