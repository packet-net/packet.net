using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.M0lteMap;

namespace Packet.Node.Tests.M0lteMap;

/// <summary>
/// The UK packet map report (#453): its exact wire form (golden), its agreement with what LinBPQ
/// actually sends for the same node (GB7RDG's own report, captured from the collector's public
/// diagnostics page), the collector's schema, which ports and neighbours are reported, and what is
/// never sent.
/// </summary>
public sealed class M0lteMapReportBuilderTests
{
    private const string Version = "0.68.0";

    /// <summary>
    /// GB7RDG's last report from LinBPQ 6.0.25.28 as the collector received it on 2026-10-17
    /// (<c>GET https://packetnodes.spots.radio/api/nodegraph/diagnostics/GB7RDG</c>, its
    /// <c>rawBody</c>), with the trailing <c>mheard</c> list cut off: that list is other
    /// stations' callsigns and has no place in this repo. Everything else is verbatim, including
    /// LinBPQ's quoted numbers.
    /// </summary>
    private const string Gb7rdgFromLinBpq = """
        {"nodeAlias": "READNG",
        "location": {"locator": "IO91lk"},
        "software": {"name": "LINBPQ","version": "6.0.25.28"},
        "source": "ReportedByNode",
        "ports": [{"id": "1","linkType": "RF","freq": "144937500","mode": "ax.25","modulation": "QPSK","baud": "1800","bitrate": "3600","usage": "Mixed","comment": "144.9375 FM QPSK3600 IL2Pc"},
        {"id": "2","linkType": "RF","freq": "432625000","mode": "ax.25","modulation": "GFSK","baud": "9600","bitrate": "9600","usage": "Mixed","comment": "432.625 GFSK9600 IL2P+CRC"},
        {"id": "3","linkType": "RF","freq": "7051600","mode": "ax.25","modulation": "BPSK300","baud": "300","bitrate": "600","usage": "Mixed","comment": "7051.6kHz BPSK300 IL2P+CRC"},
        {"id": "4","linkType": "RF","freq": "7050300","mode": "ax.25","modulation": "AFSK300","baud": "300","bitrate": "300","usage": "Mixed","comment": "7050.3 AFSK300 IL2P+CRC"},
        {"id": "5","linkType": "RF","freq": "0","mode": "ARDOP","modulation": "FSK","baud": "0","bitrate": "0","usage": "Access","comment": "7050.95kHz ARDOP 500"},
        {"id": "6","linkType": "RF","freq": "50650000","mode": "ax.25","modulation": "QPSK","baud": "1200","bitrate": "2400","usage": "Mixed","comment": "6m"},
        {"id": "7","linkType": "Internet","freq": "0","mode": "","modulation": "FSK","baud": "0","bitrate": "0","usage": "Access","comment": "Telnet"},
        {"id": "8","linkType": "Internet","freq": "0","mode": "ax.25","modulation": "FSK","baud": "0","bitrate": "0","usage": "Access","comment": "OARC-AXIP"},
        {"id": "9","linkType": "PrivateNet","freq": "0","mode": "ax.25","modulation": "AFSK1200","baud": "1200","bitrate": "1200","usage": "Mixed","comment": "Simulated RF link to pdn"}],
        "neighbours": [
        {"node": "GB7BSK", "port": "2", "quality":  "192"},
        {"node": "GB7NDH-2", "port": "8", "quality":  "50"},
        {"node": "GB7WOK", "port": "2", "quality":  "192"},
        {"node": "GB7BDH-3", "port": "8", "quality":  "50"},
        {"node": "GB7WOD", "port": "1", "quality":  "191"}]
        }
        """;

    /// <summary>The LinBPQ ports whose bpq32.cfg carries an <c>M0LTEMapInfo</c> line, so every
    /// field is the operator's and must match; the others (8) only carry what LinBPQ derives.</summary>
    private static readonly HashSet<string> PortsWithMapInfo = ["1", "2", "3", "4", "6", "9"];

    private static PortConfig Kiss(string id, int tcpPort, PortM0lteMapConfig? map = null, bool enabled = true) => new()
    {
        Id = id,
        Enabled = enabled,
        Transport = new KissTcpTransport { Host = "127.0.0.1", Port = tcpPort },
        M0lteMap = map,
    };

    private static PortM0lteMapConfig Info(
        M0lteMapLinkType type, decimal? mhz, string modulation, int baud, int bitrate, string usage, string comment) => new()
    {
        LinkType = type,
        FrequencyMhz = mhz,
        Modulation = modulation,
        Baud = baud,
        Bitrate = bitrate,
        Usage = usage,
        Comment = comment,
    };

    /// <summary>GB7RDG's bpq32.cfg in pdn terms: the same nine ports in the same order (pdn has
    /// no ARDOP or Telnet port, so 5 and 7 are placeholders that are not on the air), the same
    /// <c>M0LTEMapInfo</c> values (the last line wins where a LinBPQ port has two, as in LinBPQ),
    /// <c>MAPCOMMENT</c> is not part of this report in LinBPQ either.</summary>
    private static NodeConfig Gb7rdg() => new()
    {
        Identity = new Identity { Callsign = "GB7RDG", Alias = "READNG", Grid = "IO91lk" },
        M0lteMap = new M0lteMapConfig { Enabled = true },
        Ports =
        [
            Kiss("2m", 8910, Info(M0lteMapLinkType.RF, 144.9375m, "QPSK", 1800, 3600, "Mixed", "144.9375 FM QPSK3600 IL2Pc")),
            Kiss("70cm", 8911, Info(M0lteMapLinkType.RF, 432.625m, "GFSK", 9600, 9600, "Mixed", "432.625 GFSK9600 IL2P+CRC")),
            Kiss("40m-bpsk", 8101, Info(M0lteMapLinkType.RF, 7.0516m, "BPSK300", 300, 600, "Mixed", "7051.6kHz BPSK300 IL2P+CRC")),
            Kiss("40m-afsk", 8102, Info(M0lteMapLinkType.RF, 7.0503m, "AFSK300", 300, 300, "Mixed", "7050.3 AFSK300 IL2P+CRC")),
            Kiss("ardop-placeholder", 8200, enabled: false),
            Kiss("6m", 8913, Info(M0lteMapLinkType.RF, 50.650m, "QPSK", 1200, 2400, "Mixed", "6m")),
            Kiss("telnet-placeholder", 8201, enabled: false),
            new PortConfig
            {
                Id = "oarc-axip",
                Transport = new AxudpMultipointTransport { LocalPort = 10_093 },
                M0lteMap = new PortM0lteMapConfig { Comment = "OARC-AXIP" },
            },
            Kiss("sim", 8101 + 1000, Info(M0lteMapLinkType.PrivateNet, null, "AFSK1200", 1200, 1200, "Mixed", "Simulated RF link to pdn")),
        ],
    };

    private static M0lteMapSnapshot Gb7rdgLive() => new()
    {
        ServingPortIds = ["2m", "70cm", "40m-bpsk", "40m-afsk", "6m", "oarc-axip", "sim"],
        Neighbours =
        [
            new("GB7BDH-3", "oarc-axip", 50),
            new("GB7BSK", "70cm", 192),
            new("GB7NDH-2", "oarc-axip", 50),
            new("GB7WOD", "2m", 191),
            new("GB7WOK", "70cm", 192),
        ],
    };

    private static string Gb7rdgReportJson() =>
        M0lteMapClient.Serialize(M0lteMapReportBuilder.Build(Gb7rdg(), Gb7rdgLive(), Version)!);

    [Fact]
    public void Gb7rdg_report_is_byte_for_byte_the_golden_body()
    {
        const string golden =
            """{"nodeAlias":"READNG","location":{"locator":"IO91lk"},"software":{"name":"pdn","version":"0.68.0"}"""
            + ""","source":"ReportedByNode","ports":["""
            + """{"id":"1","linkType":"RF","freq":144937500,"mode":"ax.25","modulation":"QPSK","baud":1800,"bitrate":3600,"usage":"Mixed","comment":"144.9375 FM QPSK3600 IL2Pc"},"""
            + """{"id":"2","linkType":"RF","freq":432625000,"mode":"ax.25","modulation":"GFSK","baud":9600,"bitrate":9600,"usage":"Mixed","comment":"432.625 GFSK9600 IL2P+CRC"},"""
            + """{"id":"3","linkType":"RF","freq":7051600,"mode":"ax.25","modulation":"BPSK300","baud":300,"bitrate":600,"usage":"Mixed","comment":"7051.6kHz BPSK300 IL2P+CRC"},"""
            + """{"id":"4","linkType":"RF","freq":7050300,"mode":"ax.25","modulation":"AFSK300","baud":300,"bitrate":300,"usage":"Mixed","comment":"7050.3 AFSK300 IL2P+CRC"},"""
            + """{"id":"6","linkType":"RF","freq":50650000,"mode":"ax.25","modulation":"QPSK","baud":1200,"bitrate":2400,"usage":"Mixed","comment":"6m"},"""
            + """{"id":"8","linkType":"Internet","mode":"ax.25","comment":"OARC-AXIP"},"""
            + """{"id":"9","linkType":"PrivateNet","mode":"ax.25","modulation":"AFSK1200","baud":1200,"bitrate":1200,"usage":"Mixed","comment":"Simulated RF link to pdn"}]"""
            + ""","neighbours":[{"node":"GB7WOD","port":"1","quality":191},{"node":"GB7BSK","port":"2","quality":192},"""
            + """{"node":"GB7WOK","port":"2","quality":192},{"node":"GB7BDH-3","port":"8","quality":50},{"node":"GB7NDH-2","port":"8","quality":50}]}""";

        Gb7rdgReportJson().Should().Be(golden);
    }

    [Fact]
    public void Gb7rdg_report_says_what_LinBpq_says_for_the_same_node()
    {
        var pdn = JsonNode.Parse(Gb7rdgReportJson())!.AsObject();
        var bpq = JsonNode.Parse(Gb7rdgFromLinBpq)!.AsObject();

        foreach (var key in new[] { "nodeAlias", "source" })
        {
            pdn[key]!.GetValue<string>().Should().Be(bpq[key]!.GetValue<string>(), key);
        }
        pdn["location"]!["locator"]!.GetValue<string>().Should().Be(bpq["location"]!["locator"]!.GetValue<string>());

        var pdnPorts = pdn["ports"]!.AsArray().Select(p => p!.AsObject()).ToDictionary(p => p["id"]!.GetValue<string>());
        var bpqPorts = bpq["ports"]!.AsArray().Select(p => p!.AsObject()).ToDictionary(p => p["id"]!.GetValue<string>());

        // pdn has no ARDOP port and no Telnet port (its console is not an AX.25 port); every other
        // port LinBPQ reports, pdn reports under the same number.
        bpqPorts.Keys.Except(pdnPorts.Keys).Should().BeEquivalentTo(["5", "7"]);
        pdnPorts.Keys.Except(bpqPorts.Keys).Should().BeEmpty();

        foreach (var (id, p) in pdnPorts)
        {
            var b = bpqPorts[id];
            foreach (var field in new[] { "linkType", "mode", "comment" })
            {
                Str(p, field).Should().Be(Str(b, field), $"port {id} {field}");
            }
            if (!PortsWithMapInfo.Contains(id))
            {
                continue;   // LinBPQ's placeholders (freq 0, FSK, Access) are deliberately not copied
            }
            Num(p, "freq").Should().Be(Num(b, "freq") is 0 ? null : Num(b, "freq"), $"port {id} freq");
            foreach (var field in new[] { "baud", "bitrate" })
            {
                Num(p, field).Should().Be(Num(b, field), $"port {id} {field}");
            }
            foreach (var field in new[] { "modulation", "usage" })
            {
                Str(p, field).Should().Be(Str(b, field), $"port {id} {field}");
            }
        }

        static string Neighbour(JsonNode? n) =>
            $"{n!["node"]!.GetValue<string>()}/{n["port"]!.GetValue<string>()}/{Num(n.AsObject(), "quality")}";
        pdn["neighbours"]!.AsArray().Select(Neighbour)
            .Should().BeEquivalentTo(bpq["neighbours"]!.AsArray().Select(Neighbour));
    }

    // LinBPQ quotes its numbers; pdn sends JSON numbers, as the collector's schema types them.
    private static long? Num(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<long>(out var n) => n,
        JsonValue v => long.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(key),
    };

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

    [Fact]
    public void Report_uses_only_fields_the_collectors_schema_declares_and_never_a_heard_list()
    {
        // The collector's OpenAPI NodeData / Port / NeighbourLink schemas, all
        // additionalProperties: false.
        string[] nodeData = ["nodeAlias", "location", "software", "contact", "sysopComment", "ports", "neighbours", "source", "aliases", "mheard"];
        string[] port = ["id", "linkType", "freq", "band", "mode", "modulation", "baud", "bitrate", "usage", "comment", "facUri"];
        string[] neighbour = ["node", "port", "quality", "sntt", "type", "uri"];

        var report = JsonNode.Parse(Gb7rdgReportJson())!.AsObject();

        report.Select(kv => kv.Key).Should().BeSubsetOf(nodeData);
        report.ContainsKey("mheard").Should().BeFalse("the heard list is other stations' callsigns");
        report.ContainsKey("contact").Should().BeFalse();
        report.ContainsKey("sysopComment").Should().BeFalse("LinBPQ never sends it to this service");
        foreach (var p in report["ports"]!.AsArray())
        {
            p!.AsObject().Select(kv => kv.Key).Should().BeSubsetOf(port);
        }
        foreach (var n in report["neighbours"]!.AsArray())
        {
            n!.AsObject().Select(kv => kv.Key).Should().BeSubsetOf(neighbour);
        }
        report["ports"]!.AsArray().Select(p => p!["linkType"]!.GetValue<string>())
            .Should().BeSubsetOf(Enum.GetNames<M0lteMapLinkType>());
    }

    [Fact]
    public void Nothing_from_a_transport_endpoint_or_a_peer_list_reaches_the_report()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1", Grid = "IO91wm" },
            Ports =
            [
                new PortConfig
                {
                    Id = "axip",
                    Transport = new AxudpMultipointTransport
                    {
                        LocalPort = 10_093,
                        Peers = [new AxudpPeerConfig { Call = "G9ZZZ-7", Host = "198.51.100.23", Port = 10_093 }],
                    },
                },
                new PortConfig { Id = "vhf", Transport = new KissTcpTransport { Host = "203.0.113.9", Port = 8001 } },
            ],
        };

        var json = M0lteMapClient.Serialize(M0lteMapReportBuilder.Build(
            config, new M0lteMapSnapshot { ServingPortIds = ["axip", "vhf"] }, Version)!);

        json.Should().NotContain("198.51.100.23").And.NotContain("G9ZZZ").And.NotContain("203.0.113.9").And.NotContain("10093");
    }

    [Fact]
    public void A_port_that_is_not_on_the_air_is_left_out_and_so_are_its_neighbours()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1", Grid = "IO91wm" },
            Ports =
            [
                Kiss("vhf", 8001),
                Kiss("down", 8002),
                Kiss("uhf", 8004),
            ],
        };
        var snapshot = new M0lteMapSnapshot
        {
            ServingPortIds = ["vhf", "uhf"],
            Neighbours = [new("GB7AAA", "vhf", 200), new("GB7CCC", "down", 100)],
        };

        var report = M0lteMapReportBuilder.Build(config, snapshot, Version)!;

        report.Ports.Select(p => (p.Id, p.Comment)).Should().Equal(("1", "vhf"), ("3", "uhf"));
        report.Neighbours!.Select(n => n.Node).Should().Equal("GB7AAA");
    }

    [Fact]
    public void An_unset_detail_is_left_out_rather_than_filled_with_a_placeholder()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1", Grid = "IO91wm" },
            Ports = [Kiss("vhf", 8001), new PortConfig { Id = "ip", Transport = new AxudpTransport { Host = "127.0.0.1", Port = 10_093 } }],
        };

        var json = M0lteMapClient.Serialize(M0lteMapReportBuilder.Build(
            config, new M0lteMapSnapshot { ServingPortIds = ["vhf", "ip"] }, Version)!);

        json.Should().Be(
            """
            {"location":{"locator":"IO91wm"},"software":{"name":"pdn","version":"0.68.0"},"source":"ReportedByNode","ports":[{"id":"1","linkType":"RF","mode":"ax.25","comment":"vhf"},{"id":"2","linkType":"Internet","mode":"ax.25","comment":"ip"}]}
            """);
    }

    [Theory]
    [InlineData("144.9375", 144_937_500L)]
    [InlineData("7.0516", 7_051_600L)]
    [InlineData("10.1473", 10_147_300L)]
    [InlineData("0.1365", 136_500L)]
    public void Frequency_in_MHz_is_reported_exactly_in_Hz(string mhz, long hz)
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1", Grid = "IO91wm" },
            Ports = [Kiss("p", 8001, new PortM0lteMapConfig { FrequencyMhz = decimal.Parse(mhz, CultureInfo.InvariantCulture) })],
        };

        M0lteMapReportBuilder.Build(config, new M0lteMapSnapshot { ServingPortIds = ["p"] }, Version)!
            .Ports.Single().Freq.Should().Be(hz);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IO91")]
    [InlineData("IO91wm12")]
    [InlineData("ZZ99zz")]
    public void Without_a_6_character_locator_there_is_no_report(string? grid)
    {
        var config = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1", Grid = grid } };

        M0lteMapReportBuilder.Build(config, M0lteMapSnapshot.Empty, Version).Should().BeNull();
    }

    [Fact]
    public void A_node_with_no_neighbours_sends_no_neighbours_key_as_LinBpq_does()
    {
        var config = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1", Grid = " IO91wm " } };

        var json = M0lteMapClient.Serialize(M0lteMapReportBuilder.Build(config, M0lteMapSnapshot.Empty, Version)!);

        json.Should().Be("""{"location":{"locator":"IO91wm"},"software":{"name":"pdn","version":"0.68.0"},"source":"ReportedByNode","ports":[]}""");
        JsonDocument.Parse(json).RootElement.TryGetProperty("neighbours", out _).Should().BeFalse();
    }
}
