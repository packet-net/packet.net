using System.Text.Json.Serialization;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Oarc;

namespace Packet.Node.Core.M0lteMap;

/// <summary>
/// The report body the UK packet map's collector takes at <c>POST /api/NodeData/{nodeCall}</c>
/// (its OpenAPI <c>NodeData</c> schema). The field names and meanings are the ones LinBPQ's
/// <c>SendDataToPktMapThread</c> sends; the numbers are sent as JSON numbers, as the schema
/// types them (LinBPQ quotes them, which the collector also accepts). Nulls are left out.
/// </summary>
public sealed record M0lteMapNodeData
{
    /// <summary>The node alias (LinBPQ's <c>NODEALIAS</c>). Left out when the node has none.</summary>
    [JsonPropertyName("nodeAlias")]
    public string? NodeAlias { get; init; }

    /// <summary>Where the node is: its Maidenhead locator.</summary>
    [JsonPropertyName("location")]
    public required M0lteMapLocation Location { get; init; }

    /// <summary>What the node runs.</summary>
    [JsonPropertyName("software")]
    public required M0lteMapSoftware Software { get; init; }

    /// <summary>Always <c>ReportedByNode</c> (the collector's other value is for crawled data).</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "ReportedByNode";

    /// <summary>The serving ports that are reported, in configuration order.</summary>
    [JsonPropertyName("ports")]
    public required IReadOnlyList<M0lteMapPort> Ports { get; init; }

    /// <summary>The NET/ROM neighbours the node has an L2 link up to right now. Null (left out)
    /// when there are none, as LinBPQ does.</summary>
    [JsonPropertyName("neighbours")]
    public IReadOnlyList<M0lteMapNeighbour>? Neighbours { get; init; }
}

/// <summary>The report's <c>location</c> object.</summary>
public sealed record M0lteMapLocation
{
    /// <summary>The node's 6-character Maidenhead locator, as configured.</summary>
    [JsonPropertyName("locator")]
    public required string Locator { get; init; }
}

/// <summary>The report's <c>software</c> object.</summary>
public sealed record M0lteMapSoftware
{
    /// <summary>The software name (LinBPQ sends <c>LINBPQ</c> or <c>BPQ32</c>).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The software version.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>One entry in the report's <c>ports</c> list.</summary>
public sealed record M0lteMapPort
{
    /// <summary>The port's number, as text: its 1-based position in the node's configuration,
    /// the number the console's <c>PORTS</c> listing shows (LinBPQ sends its <c>PORTNUM</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary><c>RF</c>, <c>PrivateNet</c>, <c>Internet</c> or <c>Other</c>.</summary>
    [JsonPropertyName("linkType")]
    public required string LinkType { get; init; }

    /// <summary>The channel frequency in Hz.</summary>
    [JsonPropertyName("freq")]
    public long? Freq { get; init; }

    /// <summary>The link protocol. <c>ax.25</c> for every pdn port, as LinBPQ sends for a KISS port.</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>The modulation, free text.</summary>
    [JsonPropertyName("modulation")]
    public string? Modulation { get; init; }

    /// <summary>The symbol rate.</summary>
    [JsonPropertyName("baud")]
    public int? Baud { get; init; }

    /// <summary>The data rate in bit/s.</summary>
    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; init; }

    /// <summary>What the port is for, free text.</summary>
    [JsonPropertyName("usage")]
    public string? Usage { get; init; }

    /// <summary>The port's description.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

/// <summary>One entry in the report's <c>neighbours</c> list.</summary>
public sealed record M0lteMapNeighbour
{
    /// <summary>The neighbour node's callsign.</summary>
    [JsonPropertyName("node")]
    public required string Node { get; init; }

    /// <summary>The number (see <see cref="M0lteMapPort.Id"/>) of the port it is heard on.</summary>
    [JsonPropertyName("port")]
    public required string Port { get; init; }

    /// <summary>The NET/ROM quality of the neighbour on that port.</summary>
    [JsonPropertyName("quality")]
    public int Quality { get; init; }
}

/// <summary>
/// What the map reporter needs from the running node, captured at report time: which ports are
/// on the air and which NET/ROM neighbours it has. Everything else comes from the config.
/// </summary>
public sealed record M0lteMapSnapshot
{
    /// <summary>The ids of the ports that are serving (up or degraded) right now.</summary>
    public IReadOnlyCollection<string> ServingPortIds { get; init; } = [];

    /// <summary>The NET/ROM neighbours the node has an L2 link up to right now.</summary>
    public IReadOnlyList<M0lteMapNeighbourState> Neighbours { get; init; } = [];

    /// <summary>An empty snapshot (nothing serving, no neighbours).</summary>
    public static M0lteMapSnapshot Empty { get; } = new();
}

/// <summary>A NET/ROM neighbour with a link up: callsign, the port id the link is on, and the
/// quality the node assigns that neighbour there.</summary>
public sealed record M0lteMapNeighbourState(string Callsign, string PortId, int Quality);

/// <summary>The seam between the map reporter and the running node (so the reporter is tested
/// against a fake).</summary>
public interface IM0lteMapStateSource
{
    /// <summary>Capture the serving ports and NET/ROM neighbours now.</summary>
    M0lteMapSnapshot Capture();
}

/// <summary>
/// Builds the map report from the node's config and a <see cref="M0lteMapSnapshot"/>. Pure: the
/// same inputs give the same report, so the wire format is pinned by golden tests.
/// </summary>
public static class M0lteMapReportBuilder
{
    /// <summary>The software name pdn reports, the same one it gives the OARC node API.</summary>
    public const string SoftwareName = "pdn";

    /// <summary>The <c>mode</c> every pdn port reports: pdn ports carry AX.25, and LinBPQ sends
    /// this exact string for a KISS port.</summary>
    public const string Mode = "ax.25";

    /// <summary>
    /// Build the report, or return null when the node cannot be placed on the map (no valid
    /// 6-character Maidenhead <see cref="Identity.Grid"/>).
    /// </summary>
    /// <remarks>
    /// <para>Every serving port is reported, as LinBPQ reports every active port. Unset details
    /// are left out; LinBPQ fills them with placeholders (frequency 0, modulation FSK, usage
    /// Access) that the map would show as fact.</para>
    /// <para>Neighbours are the snapshot's: the NET/ROM neighbours with an L2 link up now, as
    /// LinBPQ sends (<c>L2STATE &gt;= 5</c>), on reported ports.</para>
    /// <para>The heard list LinBPQ also sends (every station heard, with packet counts and
    /// times) is never built here: those are other people's callsigns.</para>
    /// </remarks>
    public static M0lteMapNodeData? Build(NodeConfig config, M0lteMapSnapshot snapshot, string version)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshot);

        var grid = config.Identity.Grid?.Trim();
        if (!MaidenheadLocator.IsValid(grid))
        {
            return null;
        }

        var serving = snapshot.ServingPortIds as IReadOnlySet<string>
            ?? new HashSet<string>(snapshot.ServingPortIds, StringComparer.Ordinal);

        var ports = new List<M0lteMapPort>();
        var numberById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < config.Ports.Count; i++)
        {
            var port = config.Ports[i];
            var map = port.M0lteMap;
            if (!serving.Contains(port.Id))
            {
                continue;
            }

            var number = i + 1;
            numberById[port.Id] = number;
            ports.Add(new M0lteMapPort
            {
                Id = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LinkType = (map?.LinkType ?? DefaultLinkType(port.Transport)).ToString(),
                Freq = map?.FrequencyMhz is { } mhz ? (long)decimal.Round(mhz * 1_000_000m, MidpointRounding.AwayFromZero) : null,
                Mode = Mode,
                Modulation = Text(map?.Modulation),
                Baud = map?.Baud,
                Bitrate = map?.Bitrate,
                Usage = Text(map?.Usage),
                Comment = Text(map?.Comment) ?? port.Id,
            });
        }

        var neighbours = snapshot.Neighbours
            .Where(n => numberById.ContainsKey(n.PortId))
            .OrderBy(n => numberById[n.PortId])
            .ThenBy(n => n.Callsign, StringComparer.Ordinal)
            .Select(n => new M0lteMapNeighbour
            {
                Node = n.Callsign,
                Port = numberById[n.PortId].ToString(System.Globalization.CultureInfo.InvariantCulture),
                Quality = n.Quality,
            })
            .ToList();

        return new M0lteMapNodeData
        {
            NodeAlias = Text(config.Identity.Alias),
            Location = new M0lteMapLocation { Locator = grid },
            Software = new M0lteMapSoftware { Name = SoftwareName, Version = version },
            Ports = ports,
            Neighbours = neighbours.Count == 0 ? null : neighbours,
        };
    }

    /// <summary>The link type a port reports when its map details do not say: an AXUDP port runs
    /// over IP and is <see cref="M0lteMapLinkType.Internet"/> (LinBPQ reports its AXIP ports that
    /// way); every other pdn transport is a radio.</summary>
    public static M0lteMapLinkType DefaultLinkType(TransportConfig transport) => transport switch
    {
        AxudpTransport or AxudpMultipointTransport => M0lteMapLinkType.Internet,
        _ => M0lteMapLinkType.RF,
    };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
