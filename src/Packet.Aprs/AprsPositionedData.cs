namespace Packet.Aprs;

/// <summary>
/// Data that places something on the map: position reports, objects, items and Mic-E reports.
/// Holds the position, symbol, and everything that can ride in the 7-byte data extension or the
/// comment (APRS12c §5 Comment Field, §7 Data Extensions).
/// </summary>
/// <remarks>
/// When decoding, every recognised structured element is lifted out of the comment into its own
/// property and <see cref="Comment"/> keeps only the free text. When encoding, the elements are
/// written back in canonical order: data extension, DF bearing or storm data, weather, altitude,
/// voice frequency, free text, signpost / corridor braces, base-91 telemetry, <c>!DAO!</c>.
/// Some elements only apply to some formats; the encoder throws if one is set where the format
/// cannot carry it (for example PHG in a compressed position).
/// </remarks>
public abstract record AprsPositionedData : AprsData
{
    private protected AprsPositionedData()
    {
    }

    /// <summary>Where it is.</summary>
    public required AprsPosition Position { get; init; }

    /// <summary>How to draw it.</summary>
    public required AprsSymbol Symbol { get; init; }

    /// <summary>True if the position is in compressed (base-91) form (APRS12c §9).</summary>
    public bool IsCompressed { get; init; }

    /// <summary>The compressed format's type byte, when present.</summary>
    public AprsCompressionType? CompressionType { get; init; }

    /// <summary>Course in degrees, 1-360 clockwise from north (360 = north). 0 means unknown except
    /// in DF reports, where it means the DF station is fixed. Null when not sent or sent as dots/spaces.</summary>
    public int? CourseDegrees { get; init; }

    /// <summary>Speed over ground in knots. Null when not sent or unknown.</summary>
    public double? SpeedKnots { get; init; }

    /// <summary>Power / height / gain / directivity, if sent.</summary>
    public AprsPhg? Phg { get; init; }

    /// <summary>Pre-calculated omnidirectional radio range in miles (<c>RNGrrrr</c>, or the
    /// compressed <c>{</c> form), if sent.</summary>
    public double? RadioRangeMiles { get; init; }

    /// <summary>Omni-DF signal strength (<c>DFSshgd</c>), if sent.</summary>
    public AprsDfSignalStrength? DfSignalStrength { get; init; }

    /// <summary>Area object shape and colour (<c>Tyy/Cxx</c> with the <c>\l</c> symbol), if sent.</summary>
    public AprsAreaObject? AreaObject { get; init; }

    /// <summary>DF bearing and N/R/Q (<c>/BRG/NRQ</c>, with the <c>/\</c> symbol), if sent.</summary>
    public AprsDfBearing? DfBearing { get; init; }

    /// <summary>Altitude above mean sea level in feet (<c>/A=</c>, compressed or Mic-E altitude), if sent.</summary>
    public double? AltitudeFeet { get; init; }

    /// <summary>The <c>!DAO!</c> datum and precision form, if sent. Its extra precision is already in <see cref="Position"/>.</summary>
    public AprsDao? Dao { get; init; }

    /// <summary>Base-91 comment telemetry (<c>|...|</c>), if sent.</summary>
    public AprsCommentTelemetry? Telemetry { get; init; }

    /// <summary>A voice frequency in the APRS frequency format, if the comment starts with one.</summary>
    public AprsVoiceFrequency? Frequency { get; init; }

    /// <summary>Weather observations, when the symbol is a weather station (<c>_</c>).</summary>
    public AprsWeather? Weather { get; init; }

    /// <summary>Storm data, when the symbol is a hurricane / tropical storm (<c>@</c>).</summary>
    public AprsStorm? Storm { get; init; }

    /// <summary>The 1-3 characters shown on a signpost (<c>\m</c> symbol), sent as <c>{55}</c> in the comment.</summary>
    public string? SignpostText { get; init; }

    /// <summary>The free-text comment left after the structured elements are removed. May contain UTF-8.</summary>
    public string Comment { get; init; } = "";
}

/// <summary>
/// A station reporting its own position (APRS12c §8, §9): data type identifiers <c>!</c> and
/// <c>=</c> without a timestamp, <c>/</c> and <c>@</c> with one. <c>=</c> and <c>@</c> mean the
/// station can receive APRS messages. With the <c>_</c> symbol this is a complete weather report
/// (APRS12c §12); with <c>/\</c> and a bearing it is a DF report.
/// </summary>
public sealed record AprsPositionReport : AprsPositionedData
{
    /// <summary>When the position was valid, for non-real-time reports; null for a current position.</summary>
    public AprsTimestamp? Timestamp { get; init; }

    /// <summary>True if the station can receive APRS messages (<c>=</c> and <c>@</c>).</summary>
    public bool MessagingCapable { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => (Timestamp is null, MessagingCapable) switch
    {
        (true, false) => '!',
        (true, true) => '=',
        (false, false) => '/',
        (false, true) => '@',
    };

    internal override void Encode(Internal.InfoWriter writer)
    {
        writer.Char(DataTypeIdentifier);
        if (Timestamp is { } ts)
        {
            Internal.TimestampCodec.Write(writer, ts, allowMonthDay: false);
        }

        Internal.PositionCodec.WriteBody(writer, this);
    }
}

/// <summary>
/// An object: something placed on the map by a station on its behalf, such as a storm, an event
/// or a repeater (APRS12c §11). Data type identifier <c>;</c>. Any station may take over an
/// object by sending one with the same name; <see cref="IsAlive"/> false kills it.
/// </summary>
public sealed record AprsObjectReport : AprsPositionedData
{
    /// <summary>The object name, 1-9 printable ASCII characters (case-sensitive; spaces allowed,
    /// trailing spaces are padding and not part of the name).</summary>
    public required string Name { get; init; }

    /// <summary>True for a live object (<c>*</c>), false for a killed one (<c>_</c>).</summary>
    public bool IsAlive { get; init; } = true;

    /// <summary>
    /// When the object report was made. The spec requires one; <c>111111z</c> marks a permanent
    /// object. Null only when decoding a report that omitted it.
    /// </summary>
    public AprsTimestamp? Timestamp { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => ';';

    internal override void Encode(Internal.InfoWriter writer)
    {
        Internal.ObjectCodec.ValidateObjectName(Name, nameof(Name));
        writer.Char(';').Ascii(Name.PadRight(9)).Char(IsAlive ? '*' : '_');
        Internal.TimestampCodec.Write(
            writer,
            Timestamp ?? throw new ArgumentException("an object report must have a timestamp (APRS12c ch. 11)", nameof(Timestamp)),
            allowMonthDay: false);
        Internal.PositionCodec.WriteBody(writer, this);
    }
}

/// <summary>
/// An item: like an object but with no timestamp, meant for things that do not move, such as a
/// checkpoint (APRS12c §11). Data type identifier <c>)</c>. APRS 1.1 recommends objects instead
/// on RF.
/// </summary>
public sealed record AprsItemReport : AprsPositionedData
{
    /// <summary>The item name, 3-9 printable ASCII characters other than <c>!</c> and <c>_</c>.</summary>
    public required string Name { get; init; }

    /// <summary>True for a live item (<c>!</c>), false for a killed one (<c>_</c>).</summary>
    public bool IsAlive { get; init; } = true;

    /// <inheritdoc/>
    public override char DataTypeIdentifier => ')';

    internal override void Encode(Internal.InfoWriter writer)
    {
        Internal.ObjectCodec.ValidateItemName(Name, nameof(Name));
        writer.Char(')').Ascii(Name).Char(IsAlive ? '!' : '_');
        Internal.PositionCodec.WriteBody(writer, this);
    }
}
