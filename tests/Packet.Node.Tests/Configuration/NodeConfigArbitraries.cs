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

    private static Gen<PortConfig> PortGen(int index) =>
        from enabled in Gen.Elements(false, true)
        from transport in TransportGen(index)
        from ax25 in Ax25Gen()
        from kiss in KissGen()
        select new PortConfig
        {
            Id = $"port{index}",
            Enabled = enabled,
            Transport = transport,
            Ax25 = ax25,
            Kiss = kiss,
        };

    private static Gen<NodeConfig> NodeConfigGen() =>
        from call in CallsignGen()
        from nPorts in Gen.Choose(0, 4)
        from ports in Gen.CollectToList(Enumerable.Range(0, nPorts).Select(PortGen))
        select new NodeConfig
        {
            SchemaVersion = 1,
            Identity = new Identity { Callsign = call },
            Ports = ports.ToList(),
        };

    public static Arbitrary<NodeConfig> NodeConfig() => Arb.From(NodeConfigGen());
}
