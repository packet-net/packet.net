namespace Packet.Node.Core.Configuration;

/// <summary>
/// The kind of link a port is, as the UK packet map (<see cref="M0lteMapConfig"/>) classifies it.
/// The member names are the collector's own enum values, sent verbatim.
/// </summary>
public enum M0lteMapLinkType
{
    /// <summary>An over-the-air radio port.</summary>
    RF,

    /// <summary>A private network link (a VPN, a lab link, a simulated channel).</summary>
    PrivateNet,

    /// <summary>A link over the public internet (AXUDP / AXIP).</summary>
    Internet,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// What one port tells the UK packet map (the pdn form of LinBPQ's per-port
/// <c>M0LTEMapInfo=Type,Freq,Modulation,Baud,Bitrate,Usage</c> line plus its
/// <c>SendtoM0LTEMap</c> switch). Every field is optional: a port with no block, or a block
/// that leaves a field unset, is still reported when map reporting is on, with the unset
/// fields left out of the report rather than filled with placeholders. See
/// <c>docs/m0lte-map-reporting.md</c>.
/// </summary>
/// <remarks>
/// Read live by the map reporter on each report: an edit changes the next report and nothing
/// else (no port restart, no on-air change).
/// </remarks>
public sealed record PortM0lteMapConfig
{
    /// <summary>Whether this port appears on the map at all (LinBPQ's <c>SendtoM0LTEMap</c>).
    /// Default <c>true</c>, as in LinBPQ: once map reporting is on, every serving port is
    /// listed unless it opts out here.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The kind of link (the first <c>M0LTEMapInfo</c> field). Null = derived from
    /// the transport: AXUDP ports are <see cref="M0lteMapLinkType.Internet"/>, everything else
    /// <see cref="M0lteMapLinkType.RF"/>.</summary>
    public M0lteMapLinkType? LinkType { get; init; }

    /// <summary>The channel frequency in MHz, e.g. <c>144.9375</c> (the second
    /// <c>M0LTEMapInfo</c> field). Reported to the map in Hz. Null = not reported.</summary>
    public decimal? FrequencyMhz { get; init; }

    /// <summary>The modulation, free text, e.g. <c>QPSK</c>, <c>GFSK</c>, <c>AFSK1200</c>
    /// (the third <c>M0LTEMapInfo</c> field). Null = not reported.</summary>
    public string? Modulation { get; init; }

    /// <summary>The symbol rate in baud, e.g. <c>1800</c> for 3600 bit/s QPSK (the fourth
    /// <c>M0LTEMapInfo</c> field). Null = not reported.</summary>
    public int? Baud { get; init; }

    /// <summary>The data rate in bits per second, e.g. <c>3600</c> (the fifth
    /// <c>M0LTEMapInfo</c> field). Null = not reported.</summary>
    public int? Bitrate { get; init; }

    /// <summary>What the port is for, free text, e.g. <c>Access</c>, <c>Backbone</c>,
    /// <c>Mixed</c> (the sixth <c>M0LTEMapInfo</c> field). Null = not reported.</summary>
    public string? Usage { get; init; }

    /// <summary>The port's description on the map (LinBPQ sends the port's <c>ID=</c> text).
    /// Null = the port's <see cref="PortConfig.Id"/>.</summary>
    public string? Comment { get; init; }
}
