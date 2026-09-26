using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>The raw weather station formats (APRS12c §12 Raw Weather Reports).</summary>
public enum AprsRawWeatherFormat
{
    /// <summary><c>#</c>: Peet Bros Ultimeter-II.</summary>
    PeetBrosHash,

    /// <summary><c>*</c>: Peet Bros Ultimeter-II.</summary>
    PeetBrosStar,

    /// <summary><c>$ULTW</c>: Ultimeter 2000 packet mode.</summary>
    UltimeterPacket,

    /// <summary><c>!!</c>: Ultimeter 2000 data logging mode.</summary>
    UltimeterLogging,
}

/// <summary>
/// Raw data from a weather station, sent without reformatting (APRS12c §12). Not recommended:
/// the sending software should convert it to a complete weather report. The vendor-specific
/// data is kept as text.
/// </summary>
public sealed record AprsRawWeatherReport : AprsData
{
    /// <summary>Which station format it is.</summary>
    public required AprsRawWeatherFormat Format { get; init; }

    /// <summary>Everything after the format prefix, as sent.</summary>
    public required string Data { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => Format switch
    {
        AprsRawWeatherFormat.PeetBrosHash => '#',
        AprsRawWeatherFormat.PeetBrosStar => '*',
        AprsRawWeatherFormat.UltimeterPacket => '$',
        _ => '!',
    };

    internal override void Encode(InfoWriter writer)
    {
        writer.Ascii(Format switch
        {
            AprsRawWeatherFormat.PeetBrosHash => "#",
            AprsRawWeatherFormat.PeetBrosStar => "*",
            AprsRawWeatherFormat.UltimeterPacket => "$ULTW",
            _ => "!!",
        });
        writer.Ascii(Data);
    }
}

/// <summary>
/// A raw NMEA 0183 sentence from a GPS, sent as-is by early trackers (APRS12c §6 NMEA Data, §8).
/// Obsolete (UAP §5.20). GGA, RMC, GLL, VTG and WPL are interpreted; the symbol, if any, comes from
/// the destination address (<see cref="AprsSymbolTable.FromDestination"/>) or source SSID.
/// </summary>
public sealed record AprsNmeaReport : AprsData
{
    /// <summary>The sentence without the leading <c>$</c>, including any <c>*hh</c> checksum.</summary>
    public required string Sentence { get; init; }

    /// <summary>The talker and sentence type, e.g. <c>GPRMC</c>.</summary>
    public string SentenceType => Sentence.Length >= 5 ? Sentence[..5] : Sentence;

    /// <summary>
    /// True when the sentence ends with a <c>*hh</c> checksum. A decoded sentence's checksum always
    /// matches: one that doesn't is corrupt and is not decoded, and the encoder refuses to send it.
    /// </summary>
    public bool HasChecksum => Internal.NmeaCodec.TryReadChecksum(Sentence, out _, out _, out _);

    /// <summary>The position, for GGA, RMC, GLL and WPL.</summary>
    public AprsPosition? Position { get; init; }

    /// <summary>Whether the receiver reported a valid fix (RMC/GLL status A, GGA quality above 0), if stated.</summary>
    public bool? FixValid { get; init; }

    /// <summary>Course over ground in degrees true (RMC, VTG).</summary>
    public double? CourseDegrees { get; init; }

    /// <summary>Speed over ground in knots (RMC, VTG).</summary>
    public double? SpeedKnots { get; init; }

    /// <summary>Altitude above mean sea level in metres (GGA).</summary>
    public double? AltitudeMetres { get; init; }

    /// <summary>The UTC time of the fix (GGA, RMC, GLL).</summary>
    public TimeOnly? Time { get; init; }

    /// <summary>The waypoint name (WPL).</summary>
    public string? WaypointName { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '$';

    internal override void Encode(InfoWriter writer)
    {
        if (Sentence.Length < 5 || Sentence.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException("an NMEA sentence is printable ASCII", nameof(Sentence));
        }

        if (Internal.NmeaCodec.TryReadChecksum(Sentence, out _, out byte expected, out byte sum) && sum != expected)
        {
            throw new ArgumentException($"the NMEA checksum is {expected:X2} but the sentence sums to {sum:X2}", nameof(Sentence));
        }

        writer.Char('$').Ascii(Sentence);
    }
}

/// <summary>
/// A Maidenhead locator beacon, data type <c>[</c>: <c>[IO91SX] comment</c> (APRS12c §8). Obsolete.
/// </summary>
public sealed record AprsMaidenheadBeacon : AprsData
{
    /// <summary>The 4 or 6 character locator.</summary>
    public required string Locator { get; init; }

    /// <summary>Text after the closing bracket.</summary>
    public string Comment { get; init; } = "";

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '[';

    internal override void Encode(InfoWriter writer)
    {
        if (!Maidenhead.IsLocator(System.Text.Encoding.ASCII.GetBytes(Locator)))
        {
            throw new ArgumentException($"'{Locator}' is not a 4 or 6 character Maidenhead locator", nameof(Locator));
        }

        Internal.Text.RequireNoLineBreaks(Comment, nameof(Comment));
        writer.Char('[').Ascii(Locator.ToUpperInvariant()).Char(']').Utf8(Comment);
    }
}

/// <summary>The target area of a general query: floating-point degrees and a radius in miles (APRS12c §15).</summary>
/// <param name="Latitude">Degrees, positive north.</param>
/// <param name="Longitude">Degrees, positive east.</param>
/// <param name="RadiusMiles">Radius, 0-9999 whole miles.</param>
public readonly record struct AprsQueryFootprint(decimal Latitude, decimal Longitude, int RadiusMiles);

/// <summary>
/// A general query to all stations, data type <c>?</c>: <c>?APRS?</c>, <c>?IGATE?</c> or
/// <c>?WX?</c>, optionally limited to a footprint (APRS12c §15). One-shot and never acknowledged.
/// IGates must not forward general queries from RF to APRS-IS (UAP §2.8.1).
/// </summary>
public sealed record AprsGeneralQuery : AprsData
{
    /// <summary>The query type, e.g. <c>APRS</c>, <c>IGATE</c>, <c>WX</c>.</summary>
    public required string QueryType { get; init; }

    /// <summary>The area the query is for, or null for everyone.</summary>
    public AprsQueryFootprint? Footprint { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '?';

    internal override void Encode(InfoWriter writer) => QueryCodec.WriteGeneral(writer, this);
}

/// <summary>One station capability: a token, optionally with <c>=value</c> (APRS12c §15).</summary>
/// <param name="Token">e.g. <c>IGATE</c>, <c>MSG_CNT</c>.</param>
/// <param name="Value">The value after <c>=</c>, or null for a bare token.</param>
public readonly record struct AprsCapability(string Token, string? Value);

/// <summary>
/// Station capabilities, data type <c>&lt;</c>: comma-separated tokens such as
/// <c>&lt;IGATE,MSG_CNT=43,LOC_CNT=14</c>, sent in reply to a <c>?IGATE?</c> query (APRS12c §15).
/// </summary>
public sealed record AprsStationCapabilities : AprsData
{
    /// <summary>The capabilities, in order.</summary>
    public required IReadOnlyList<AprsCapability> Capabilities { get; init => field = EquatableList<AprsCapability>.Of(value); }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '<';

    internal override void Encode(InfoWriter writer)
    {
        writer.Char('<');
        for (int i = 0; i < Capabilities.Count; i++)
        {
            AprsCapability c = Capabilities[i];
            if (c.Token.Length == 0 || c.Token.Any(ch => ch is <= ' ' or '\x7F' or ',' or '=') || (c.Value is { } v && v.Any(ch => ch is < ' ' or '\x7F' or ',')))
            {
                throw new ArgumentException("capability tokens and values are text without ',' or control characters (and tokens without '=' or spaces)", nameof(Capabilities));
            }

            if (i > 0)
            {
                writer.Char(',');
            }

            writer.Utf8(c.Token);
            if (c.Value is { } value)
            {
                writer.Char('=').Utf8(value);
            }
        }
    }
}

/// <summary>
/// Third-party traffic, data type <c>}</c>: a whole packet from another network (usually APRS-IS,
/// relayed to RF by an IGate) carried inside this one (APRS12c §17). Digipeaters look only at the
/// outer path; applications decode <see cref="Packet"/>. An RF-to-IS IGate must not forward it if
/// the inner path contains <c>TCPIP</c>, which prevents loops.
/// </summary>
public sealed record AprsThirdPartyTraffic : AprsData
{
    /// <summary>The encapsulated packet. Its source need not be a valid AX.25 address.</summary>
    public required AprsPacket Packet { get; init; }

    /// <summary>True if the inner path contains <c>TCPIP</c>, meaning it came from the internet.</summary>
    public bool CameFromInternet => Packet.Path.Any(p => p.Address.Value == "TCPIP");

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '}';

    internal override void Encode(InfoWriter writer) => writer.Char('}').Bytes(Packet.ToTnc2());
}

/// <summary>
/// User-defined data, data type <c>{</c>: a one-character user ID, a one-character packet type,
/// then data in a format of the author's choosing (APRS12c §19). <c>{{</c> is for experiments.
/// </summary>
public sealed record AprsUserDefinedData : AprsData
{
    /// <summary>The user ID, e.g. <c>Q</c>; <c>{</c> means experimental.</summary>
    public required char UserId { get; init; }

    /// <summary>The user-defined packet type.</summary>
    public required char PacketType { get; init; }

    /// <summary>The rest of the information field, as sent.</summary>
    public required IReadOnlyList<byte> Data { get; init => field = EquatableList<byte>.Of(value); }

    /// <summary>The data as text (UTF-8, Latin-1 where invalid).</summary>
    public string DataText => Internal.Text.ForDisplay(Data.ToArray());

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '{';

    internal override void Encode(InfoWriter writer)
    {
        if (UserId is < '!' or > '~' || PacketType is < '!' or > '~')
        {
            throw new ArgumentException("user ID and packet type are printable ASCII");
        }

        writer.Char('{').Char(UserId).Char(PacketType).Bytes(Data.ToArray());
    }
}

/// <summary>Invalid or test data, data type <c>,</c> (APRS12c §20), e.g. a Mic-E unit's invalid GPS fix.</summary>
public sealed record AprsTestData : AprsData
{
    /// <summary>Everything after the comma.</summary>
    public required string Data { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => ',';

    internal override void Encode(InfoWriter writer)
    {
        Internal.Text.RequireNoLineBreaks(Data, nameof(Data));
        writer.Char(',').Utf8(Data);
    }
}

/// <summary>An Agrelo DFJr / MicroFinder bearing, data type <c>%</c>: <c>%bbb/q</c> (APRS12c Appendix 1).</summary>
public sealed record AprsAgreloDfReport : AprsData
{
    /// <summary>Bearing in degrees, 0-360.</summary>
    public required int BearingDegrees { get; init; }

    /// <summary>Signal quality, 0-9.</summary>
    public required int Quality { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => '%';

    internal override void Encode(InfoWriter writer)
    {
        if (BearingDegrees is < 0 or > 360 || Quality is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(BearingDegrees), "bearing 0-360, quality 0-9");
        }

        writer.Char('%').Digits(BearingDegrees, 3).Char('/').Digits(Quality, 1);
    }
}
