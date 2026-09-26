using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>
/// The common end of every fluent builder started from an <see cref="AprsStation"/>:
/// <see cref="ToData"/> for the data record alone, <see cref="Build"/> for the whole packet.
/// </summary>
public abstract class AprsBuilder
{
    private protected AprsBuilder(AprsStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        Station = station;
    }

    /// <summary>The sending station, whose header the packet gets.</summary>
    public AprsStation Station { get; }

    /// <summary>The data record built so far, without a header. Nothing is checked until it is encoded.</summary>
    public abstract AprsData ToData();

    /// <summary>
    /// The packet: the station's header and this data, encoded. Throws <see cref="ArgumentException"/>
    /// for anything APRS12c does not allow, and <see cref="InvalidOperationException"/> when
    /// something the packet needs, such as a symbol, was never given.
    /// </summary>
    public virtual AprsPacket Build() => AprsPacket.Create(Station.Source, Station.Destination, ToData(), Station.Path);
}

/// <summary>Sends a data record built by hand, from <see cref="AprsStation.Data(AprsData)"/>.</summary>
public sealed class AprsDataBuilder : AprsBuilder
{
    private readonly AprsData data;

    internal AprsDataBuilder(AprsStation station, AprsData data)
        : base(station)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.data = data;
    }

    /// <inheritdoc/>
    public override AprsData ToData() => data;

    /// <inheritdoc/>
    public override AprsPacket Build() => data is AprsMicEReport micE
        ? AprsPacket.CreateMicE(Station.Source, micE, Station.Path)
        : base.Build();
}

/// <summary>
/// What every report that puts something on the map shares: position, symbol, course and speed,
/// altitude, comment and the rest (APRS12c §5-§9). <typeparamref name="TSelf"/> is the concrete
/// builder, so each call returns it.
/// </summary>
/// <typeparam name="TSelf">The concrete builder.</typeparam>
public abstract class AprsPositionedBuilder<TSelf> : AprsBuilder
    where TSelf : AprsPositionedBuilder<TSelf>
{
    private AprsPosition? position;
    private AprsSymbol? symbol;
    private int ambiguity;
    private bool compressed;
    private int? course;
    private double? speedKnots;
    private double? altitudeFeet;
    private AprsPhg? phg;
    private double? rangeMiles;
    private AprsVoiceFrequency? frequency;
    private AprsDao? dao;
    private AprsCommentTelemetry? telemetry;
    private string comment = "";

    private protected AprsPositionedBuilder(AprsStation station, AprsPosition? position)
        : base(station) => this.position = position;

    private TSelf Self => (TSelf)this;

    /// <summary>Where it is, in decimal degrees: north and east positive.</summary>
    public TSelf At(double latitude, double longitude) => At(new AprsPosition(latitude, longitude, ambiguity));

    /// <summary>Where it is.</summary>
    public TSelf At(AprsPosition position)
    {
        this.position = position;
        ambiguity = position.Ambiguity;
        return Self;
    }

    /// <summary>How to draw it, e.g. <see cref="AprsSymbol.Car"/>.</summary>
    public TSelf Symbol(AprsSymbol symbol)
    {
        this.symbol = symbol;
        return Self;
    }

    /// <summary>Course over ground in degrees clockwise from north, 1-360 (360 is north).</summary>
    public TSelf Course(int degrees)
    {
        course = degrees;
        return Self;
    }

    /// <summary>Speed over ground in knots.</summary>
    public TSelf Speed(double knots)
    {
        speedKnots = knots;
        return Self;
    }

    /// <summary>Speed over ground in kilometres per hour, sent in knots.</summary>
    public TSelf SpeedKmh(double kilometresPerHour) => Speed(kilometresPerHour / 1.852);

    /// <summary>Altitude above mean sea level in feet (<c>/A=</c>).</summary>
    public TSelf Altitude(double feet)
    {
        altitudeFeet = feet;
        return Self;
    }

    /// <summary>Altitude above mean sea level in metres, sent in feet.</summary>
    public TSelf AltitudeMetres(double metres) => Altitude(metres * Units.FeetPerMetre);

    /// <summary>Free text after the structured parts. May contain UTF-8.</summary>
    public TSelf Comment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        comment = text;
        return Self;
    }

    /// <summary>Power, antenna height, gain and directivity codes (<c>PHGphgd</c>, APRS12c §7).</summary>
    public TSelf Phg(AprsPhg phg)
    {
        this.phg = phg;
        return Self;
    }

    /// <summary>Omnidirectional radio range in miles (<c>RNGrrrr</c>, APRS12c §7).</summary>
    public TSelf Range(double miles)
    {
        rangeMiles = miles;
        return Self;
    }

    /// <summary>
    /// A voice frequency at the start of the comment, as radios with a tune button read it
    /// (APRS12c §18): e.g. <c>Frequency(145.725, tone: 118.8, offsetKHz: -600)</c> sends
    /// <c>145.725MHz T118 -060</c>. The tone is a CTCSS access tone in Hz; its tenths are not sent.
    /// </summary>
    public TSelf Frequency(double mhz, double? tone = null, int? offsetKHz = null) => Frequency(new AprsVoiceFrequency
    {
        FrequencyMHz = (decimal)mhz,
        ToneType = tone is null ? null : AprsToneType.Tone,
        ToneValue = tone is { } t ? (int)Math.Truncate(t) : null,
        OffsetKHz = offsetKHz,
    });

    /// <summary>A voice frequency at the start of the comment, with every option (APRS12c §18).</summary>
    public TSelf Frequency(AprsVoiceFrequency frequency)
    {
        ArgumentNullException.ThrowIfNull(frequency);
        this.frequency = frequency;
        return Self;
    }

    /// <summary>
    /// Sends the position in compressed (base-91) form: shorter and more precise, but with no room
    /// for PHG or other data extensions (APRS12c §9).
    /// </summary>
    public TSelf Compressed(bool compressed = true)
    {
        this.compressed = compressed;
        return Self;
    }

    /// <summary>Blanks the last 1-4 digits of the position to hide it: to about 0.1, 1, 10 or 60
    /// nautical miles (APRS12c §6 Position Ambiguity). Not with <see cref="Compressed"/>.</summary>
    public TSelf Ambiguity(int digits)
    {
        ambiguity = digits;
        if (position is { } p)
        {
            position = p with { Ambiguity = digits };
        }

        return Self;
    }

    /// <summary>Adds a <c>!DAO!</c> extension carrying extra position precision, by default the WGS84
    /// base-91 form (about 0.2 m).</summary>
    public TSelf Dao(AprsDao? dao = null)
    {
        this.dao = dao ?? AprsDao.Wgs84Base91;
        return Self;
    }

    /// <summary>Base-91 telemetry in the comment (<c>|ss1122..|</c>, APRS12c §13): a sequence
    /// number and 1-5 analog values, each 0-8280.</summary>
    public TSelf Telemetry(int sequence, params int[] analog) =>
        Telemetry(new AprsCommentTelemetry { Sequence = sequence, Analog = analog });

    /// <summary>Base-91 telemetry in the comment with the 8 digital bits (bit 0 is B1), which
    /// can only follow all 5 analog values.</summary>
    public TSelf Telemetry(int sequence, int[] analog, byte digital) =>
        Telemetry(new AprsCommentTelemetry { Sequence = sequence, Analog = analog, Digital = digital });

    /// <summary>Base-91 telemetry in the comment (APRS12c §13).</summary>
    public TSelf Telemetry(AprsCommentTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        this.telemetry = telemetry;
        return Self;
    }

    /// <inheritdoc/>
    public abstract override AprsPositionedData ToData();

    private protected AprsPosition RequirePosition(string what) =>
        position ?? throw new InvalidOperationException($"{what} needs a position: call At(latitude, longitude)");

    private protected AprsSymbol RequireSymbol(string what) =>
        symbol ?? throw new InvalidOperationException($"{what} needs a symbol: call Symbol(...), e.g. Symbol(AprsSymbol.Car)");

    /// <summary>Copies the shared fields onto <paramref name="data"/>, which carries only its own.</summary>
    private protected T Apply<T>(T data)
        where T : AprsPositionedData => (T)(data with
        {
            IsCompressed = compressed,
            CourseDegrees = course,
            SpeedKnots = speedKnots,
            AltitudeFeet = altitudeFeet,
            Phg = phg,
            RadioRangeMiles = rangeMiles,
            Frequency = frequency,
            Dao = dao,
            Telemetry = telemetry,
            Comment = comment,
        });
}

/// <summary>A position report, from <see cref="AprsStation.Position(double, double)"/> (APRS12c §8, §9).</summary>
public sealed class AprsPositionBuilder : AprsPositionedBuilder<AprsPositionBuilder>
{
    private bool messaging;
    private AprsTimestamp? timestamp;

    internal AprsPositionBuilder(AprsStation station, AprsPosition position)
        : base(station, position)
    {
    }

    /// <summary>Says the station can receive APRS messages (<c>=</c> or <c>@</c> rather than <c>!</c> or <c>/</c>).</summary>
    public AprsPositionBuilder Messaging(bool capable = true)
    {
        messaging = capable;
        return this;
    }

    /// <summary>When the position was valid, for a report that is not current (APRS12c §6).</summary>
    public AprsPositionBuilder Timestamp(AprsTimestamp timestamp)
    {
        this.timestamp = timestamp;
        return this;
    }

    /// <summary>When the position was valid, sent as day, hours and minutes UTC (<c>DDHHMMz</c>).</summary>
    public AprsPositionBuilder Timestamp(DateTime utc) => Timestamp(AprsTimestamp.FromDateTime(utc));

    /// <inheritdoc/>
    public override AprsPositionReport ToData() => Apply(new AprsPositionReport
    {
        Position = RequirePosition("a position report"),
        Symbol = RequireSymbol("a position report"),
        MessagingCapable = messaging,
        Timestamp = timestamp,
    });
}

/// <summary>An object, from <see cref="AprsStation.Object(string)"/> (APRS12c §11).</summary>
public sealed class AprsObjectBuilder : AprsPositionedBuilder<AprsObjectBuilder>
{
    private readonly string name;
    private bool alive = true;
    private AprsTimestamp? timestamp;

    internal AprsObjectBuilder(AprsStation station, string name)
        : base(station, null)
    {
        ArgumentNullException.ThrowIfNull(name);
        this.name = name;
    }

    /// <summary>When the object report was made. Without this, the time of <c>ToData()</c> or <c>Build()</c>, to the minute.</summary>
    public AprsObjectBuilder Timestamp(AprsTimestamp timestamp)
    {
        this.timestamp = timestamp;
        return this;
    }

    /// <summary>When the object report was made, sent as day, hours and minutes UTC (<c>DDHHMMz</c>).</summary>
    public AprsObjectBuilder Timestamp(DateTime utc) => Timestamp(AprsTimestamp.FromDateTime(utc));

    /// <summary>Marks the object permanent, one only this station may change, with the
    /// <c>111111z</c> timestamp (APRS12c §18 Object Name Permanence).</summary>
    public AprsObjectBuilder Permanent() => Timestamp(AprsTimestamp.DayHoursMinutes(11, 11, 11));

    /// <summary>Kills the object, removing it from maps (<c>_</c> rather than <c>*</c>).</summary>
    public AprsObjectBuilder Kill()
    {
        alive = false;
        return this;
    }

    /// <inheritdoc/>
    public override AprsObjectReport ToData() => Apply(new AprsObjectReport
    {
        Name = name,
        IsAlive = alive,
        Timestamp = timestamp ?? AprsTimestamp.FromDateTime(DateTime.UtcNow),
        Position = RequirePosition("an object"),
        Symbol = RequireSymbol("an object"),
    });
}

/// <summary>An item, from <see cref="AprsStation.Item(string)"/> (APRS12c §11).</summary>
public sealed class AprsItemBuilder : AprsPositionedBuilder<AprsItemBuilder>
{
    private readonly string name;
    private bool alive = true;

    internal AprsItemBuilder(AprsStation station, string name)
        : base(station, null)
    {
        ArgumentNullException.ThrowIfNull(name);
        this.name = name;
    }

    /// <summary>Kills the item, removing it from maps (<c>_</c> rather than <c>!</c>).</summary>
    public AprsItemBuilder Kill()
    {
        alive = false;
        return this;
    }

    /// <inheritdoc/>
    public override AprsItemReport ToData() => Apply(new AprsItemReport
    {
        Name = name,
        IsAlive = alive,
        Position = RequirePosition("an item"),
        Symbol = RequireSymbol("an item"),
    });
}

/// <summary>
/// A Mic-E position report, from <see cref="AprsStation.MicE(double, double)"/> (APRS12c §10). The
/// position comment defaults to <see cref="AprsMicEMessage.OffDuty"/>; <see cref="AprsMicEMessage.Emergency"/>
/// sets off alarms, so is only sent when asked for.
/// </summary>
public sealed class AprsMicEBuilder : AprsPositionedBuilder<AprsMicEBuilder>
{
    private AprsMicEMessage message = AprsMicEMessage.OffDuty;
    private bool messaging;

    internal AprsMicEBuilder(AprsStation station, AprsPosition position)
        : base(station, position)
    {
    }

    /// <summary>The position comment, e.g. <see cref="AprsMicEMessage.EnRoute"/>.</summary>
    public AprsMicEBuilder Message(AprsMicEMessage message)
    {
        this.message = message;
        return this;
    }

    /// <summary>Says the station can receive APRS messages (the <c>`</c> type code, APRS 1.2).</summary>
    public AprsMicEBuilder Messaging(bool capable = true)
    {
        messaging = capable;
        return this;
    }

    /// <inheritdoc/>
    public override AprsMicEReport ToData() => Apply(new AprsMicEReport
    {
        Position = RequirePosition("a Mic-E report"),
        Symbol = RequireSymbol("a Mic-E report"),
        Message = message,
        TypeCode = messaging ? '`' : null,
    });

    /// <inheritdoc/>
    public override AprsPacket Build() => AprsPacket.CreateMicE(Station.Source, ToData(), Station.Path);
}
