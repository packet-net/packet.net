namespace Packet.Aprs.Internal;

/// <summary>Mutable accumulator for the fields shared by every <see cref="AprsPositionedData"/> while decoding.</summary>
internal sealed class PositionedFields
{
    public AprsPosition Position { get; set; }

    public AprsSymbol Symbol { get; set; }

    public bool IsCompressed { get; set; }

    public AprsCompressionType? CompressionType { get; set; }

    public int? CourseDegrees { get; set; }

    public double? SpeedKnots { get; set; }

    public AprsPhg? Phg { get; set; }

    public double? RadioRangeMiles { get; set; }

    public AprsDfSignalStrength? DfSignalStrength { get; set; }

    public AprsAreaObject? AreaObject { get; set; }

    public AprsDfBearing? DfBearing { get; set; }

    public double? AltitudeFeet { get; set; }

    public AprsDao? Dao { get; set; }

    public AprsCommentTelemetry? Telemetry { get; set; }

    public AprsVoiceFrequency? Frequency { get; set; }

    public AprsWeather? Weather { get; set; }

    public AprsStorm? Storm { get; set; }

    public string? SignpostText { get; set; }

    public string Comment { get; set; } = "";

    /// <summary>Copies the accumulated fields onto a freshly constructed report.</summary>
    public T ApplyTo<T>(T target)
        where T : AprsPositionedData =>
        (T)((AprsPositionedData)target with
        {
            Position = Position,
            Symbol = Symbol,
            IsCompressed = IsCompressed,
            CompressionType = CompressionType,
            CourseDegrees = CourseDegrees,
            SpeedKnots = SpeedKnots,
            Phg = Phg,
            RadioRangeMiles = RadioRangeMiles,
            DfSignalStrength = DfSignalStrength,
            AreaObject = AreaObject,
            DfBearing = DfBearing,
            AltitudeFeet = AltitudeFeet,
            Dao = Dao,
            Telemetry = Telemetry,
            Frequency = Frequency,
            Weather = Weather,
            Storm = Storm,
            SignpostText = SignpostText,
            Comment = Comment,
        });
}
