using FsCheck;
using FsCheck.Fluent;

namespace Packet.Aprs.Tests.Properties;

/// <summary>
/// Randomised checks over valid data of every kind: encoding then decoding gives the same data
/// back, and re-encoding the decoded data gives the same bytes. Plus: no input makes the decoder
/// throw (other than an unusable header).
/// </summary>
public class RoundTripProperties
{
    private const int Runs = 2000;

    private static readonly Gen<string> Word =
        Gen.Elements("Hello", "Test", "digi", "Net", "QRV", "mobile", "73", "de", "M0LTE", "Pi", "ÅÄÖ", "テスト", "°C");

    private static readonly Gen<string> Comment =
        from n in Gen.Choose(0, 5)
        from words in Gen.ArrayOf(Word, n)
        from lead in Gen.Elements("", "", "", " ", "/", "  ")
        select lead + string.Join(' ', words);

    // Positions on the grid the uncompressed format can carry: whole hundredths of a minute.
    private static readonly Gen<AprsPosition> GridPosition =
        from lat in Gen.Choose(0, (90 * 6000) - 1)
        from lon in Gen.Choose(0, (180 * 6000) - 1)
        from south in Gen.Elements(false, true)
        from west in Gen.Elements(false, true)
        from ambiguity in Gen.Frequency((6, Gen.Constant(0)), (1, Gen.Choose(1, 4)))
        select Masked(lat, lon, south, west, ambiguity);

    private static readonly Gen<AprsSymbol> Symbol =
        from table in Gen.Elements('/', '\\', 'A', 'Z', '3')
        from code in Gen.Choose('!', '~').Select(c => (char)c).Where(c => c is not ('_' or '@' or '\\' or 'l' or 'm'))
        select new AprsSymbol(table, code);

    private static readonly Gen<AprsTimestamp?> Timestamp = Gen.OneOf(
        Gen.Constant<AprsTimestamp?>(null),
        from d in Gen.Choose(1, 31) from h in Gen.Choose(0, 23) from m in Gen.Choose(0, 59) select (AprsTimestamp?)AprsTimestamp.DayHoursMinutes(d, h, m),
        from h in Gen.Choose(0, 23) from m in Gen.Choose(0, 59) from s in Gen.Choose(0, 59) select (AprsTimestamp?)AprsTimestamp.HoursMinutesSeconds(h, m, s));

    private static readonly Gen<AprsVoiceFrequency?> Frequency = Gen.OneOf(
        Gen.Constant<AprsVoiceFrequency?>(null),
        from khz in Gen.Choose(144000, 147999)
        from tone in Gen.Elements<AprsToneType?>(null, AprsToneType.Tone, AprsToneType.Ctcss, AprsToneType.Off)
        from offset in Gen.Elements<int?>(null, -600, 600, 5000)
        select (AprsVoiceFrequency?)new AprsVoiceFrequency
        {
            FrequencyMHz = khz / 1000m,
            ToneType = tone,
            ToneValue = tone is AprsToneType.Tone or AprsToneType.Ctcss ? 100 : null,
            OffsetKHz = offset,
        });

    private static readonly Gen<AprsCommentTelemetry?> Telemetry = Gen.OneOf(
        Gen.Constant<AprsCommentTelemetry?>(null),
        from seq in Gen.Choose(0, 8280)
        from n in Gen.Choose(1, 5)
        from values in Gen.ArrayOf(Gen.Choose(0, 8280), n)
        select (AprsCommentTelemetry?)new AprsCommentTelemetry { Sequence = seq, Analog = values });

    private static readonly Gen<(int? Course, double? Speed, AprsPhg? Phg, double? Range)> Extension = Gen.OneOf(
        Gen.Constant<(int?, double?, AprsPhg?, double?)>((null, null, null, null)),
        from course in Gen.Choose(1, 360) from speed in Gen.Choose(0, 999) select ((int?)course, (double?)speed, (AprsPhg?)null, (double?)null),
        from p in Gen.Choose(0, 9) from h in Gen.Choose(0, 12) from g in Gen.Choose(0, 9) from d in Gen.Choose(0, 8)
        from rate in Gen.Elements<int?>(null, null, 4, 12)
        select ((int?)null, (double?)null, (AprsPhg?)new AprsPhg(p, h, g, d, rate), (double?)null),
        from r in Gen.Choose(0, 9999) select ((int?)null, (double?)null, (AprsPhg?)null, (double?)r));

    private static readonly Gen<AprsPositionReport> UncompressedReport =
        from position in GridPosition
        from symbol in Symbol
        from timestamp in Timestamp
        from messaging in Gen.Elements(false, true)
        from ext in Extension
        from altitude in Gen.OneOf(Gen.Constant<double?>(null), Gen.Choose(-99999, 999999).Select(a => (double?)a))
        from frequency in Frequency
        from comment in Comment
        from telemetry in Telemetry
        select new AprsPositionReport
        {
            Position = position,
            Symbol = symbol,
            Timestamp = timestamp,
            MessagingCapable = messaging,
            CourseDegrees = ext.Course,
            SpeedKnots = ext.Speed,
            Phg = ext.Phg,
            RadioRangeMiles = ext.Range,
            AltitudeFeet = altitude,
            Frequency = frequency,
            Comment = comment,
            Telemetry = telemetry,
        };

    // Compressed positions: values on the base-91 grid, with each kind of cs content.
    private static readonly Gen<AprsPositionReport> CompressedReport =
        from y in Gen.Choose(0, (int)(180 * 380926.0))
        from x in Gen.Choose(0, (int)(360 * 190463.0))
        from symbol in Symbol.Where(s => s.Table is not (>= '0' and <= '9'))
        from cs in Gen.Choose(0, 3)
        from c in Gen.Choose(1, 89)
        from sp in Gen.Choose(0, 90)
        from comment in Comment.Select(c => c.TrimStart(' ', '/'))
        select new AprsPositionReport
        {
            Position = new AprsPosition(90 - (y / 380926.0), -180 + (x / 190463.0)),
            Symbol = symbol,
            IsCompressed = true,
            CourseDegrees = cs == 1 ? c * 4 : null,
            SpeedKnots = cs == 1 ? Math.Pow(1.08, sp) - 1 : null,
            RadioRangeMiles = cs == 2 ? 2 * Math.Pow(1.08, sp) : null,
            AltitudeFeet = cs == 3 ? Math.Pow(1.002, (c * 91) + sp) : null,
            CompressionType = cs == 0 ? null : new AprsCompressionType(AprsGpsFix.Current, cs == 3 ? AprsNmeaSource.Gga : AprsNmeaSource.Rmc, AprsCompressionOrigin.Software),
            Comment = comment,
        };

    private static readonly Gen<AprsMicEReport> MicEReport =
        from position in GridPosition
        from symbol in Symbol
        from course in Gen.OneOf(Gen.Constant<int?>(null), Gen.Choose(1, 360).Select(c => (int?)c))
        from speed in Gen.Choose(0, 799)
        from message in Gen.Elements(Enum.GetValues<AprsMicEMessage>().Where(m => m != AprsMicEMessage.Unknown).ToArray())
        from type in Gen.Elements<char?>(null, '`', '\'', '>', ']')
        from metres in Gen.OneOf(Gen.Constant<int?>(null), Gen.Choose(-10000, 50000).Select(m => (int?)m))
        from locator in Gen.Elements<string?>(null, null, "IO91", "IO91SX", "FN42KW")
        from comment in Comment
        select new AprsMicEReport
        {
            Position = position,
            Symbol = symbol,
            CourseDegrees = course,
            SpeedKnots = speed,
            Message = message,
            TypeCode = type,
            MaidenheadLocator = locator,
            AltitudeFeet = type is null ? null : metres * (1 / 0.3048),
            Comment = type is null && comment.Length > 0 && comment[0] is '`' or '\'' or '>' or ']' or ' ' ? "x" + comment : comment,
        };

    private static readonly Gen<AprsTextMessage> TextMessage =
        from call in Gen.Elements("N0CALL", "M0LTE-9", "WHO-IS", "EMAIL", "G3NRW-15")
        from text in Comment.Select(c => c.Length > 67 ? c[..67] : c)
        from id in Gen.Elements<string?>(null, "1", "001", "AB12x")
        from reply in Gen.Elements<string?>(null, "", "7")
        select new AprsTextMessage { Addressee = call, Text = "msg " + text, MessageId = id, ReplyAck = id is null ? null : reply };

    [Fact]
    public void Uncompressed_position_reports_round_trip() => Check(UncompressedReport);

    [Fact]
    public void Compressed_position_reports_round_trip() => Check(CompressedReport);

    [Fact]
    public void Mic_e_reports_round_trip() => Check(MicEReport, micE: true);

    [Fact]
    public void Text_messages_round_trip() => Check(TextMessage);

    [Fact]
    public void Decoding_random_information_fields_never_throws()
    {
        Gen<byte[]> info = Gen.OneOf(
            Gen.ArrayOf(Gen.Choose(0, 255).Select(b => (byte)b)),
            from dti in Gen.Elements("!", "=", "/", "@", "`", "'", ";", ")", ":", ">", "T#", "_", "$", "[", "?", "<", "}", "{", ",", "%", "!!")
            from rest in Gen.ArrayOf(Gen.Elements("0", "1", "9", ".", "N", "S", "E", "W", "/", "\\", "_", "*", "{", "}", "|", "!", " ", "A", "z", ":", ">", ","))
            select System.Text.Encoding.ASCII.GetBytes(dti + string.Concat(rest)));
        Gen<string> destination = Gen.Elements("APZ001", "S32UVT", "T2TQ5U", "LLLLLL", "ZZZZZZ", "GPSMV");

        Prop.ForAll(Arb.From(info), Arb.From(destination), (bytes, dest) =>
        {
            AprsPacket packet = AprsPacket.Decode(AprsAddress.Parse("N0CALL"), AprsAddress.Parse(dest), [], bytes);
            AprsPacket strict = AprsPacket.Decode(AprsAddress.Parse("N0CALL"), AprsAddress.Parse(dest), [], bytes, AprsParseOptions.Strict);
            return packet.Data is not null && strict.Data is not null;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Decoding_random_lines_only_ever_throws_a_format_exception()
    {
        Gen<string> line = Gen.ArrayOf(Gen.Elements("N0CALL", ">", ",", ":", "*", "APZ", "-1", "!", " ", "WIDE1-1", "qAR", "}", "x"))
            .Select(string.Concat);
        Prop.ForAll(Arb.From(line), text =>
        {
            try
            {
                AprsPacket.Decode(text);
            }
            catch (AprsFormatException)
            {
            }

            return true;
        }).QuickCheckThrowOnFailure();
    }

    private static void Check<T>(Gen<T> gen, bool micE = false)
        where T : AprsData
    {
        Config config = Config.QuickThrowOnFailure.WithMaxTest(Runs);
        Prop.ForAll(Arb.From(gen), data =>
        {
            AprsPacket first = micE
                ? AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), (AprsMicEReport)(object)data)
                : AprsPacket.Create(AprsAddress.Parse("N0CALL"), AprsAddress.Parse("APZ001"), data);
            AprsPacket decoded = AprsPacket.Decode(first.ToTnc2());
            if (decoded.HasErrors || decoded.HasWarnings)
            {
                throw new InvalidOperationException($"{first} decoded with: {string.Join(" | ", decoded.Diagnostics)}");
            }

            byte[] again = decoded.Data.ToInformationField();
            if (!again.AsSpan().SequenceEqual(first.Information.Span))
            {
                throw new InvalidOperationException($"{first} re-encoded as {System.Text.Encoding.UTF8.GetString(again)}");
            }

            if (!SameIgnoringFloatingPoint(data, decoded.Data))
            {
                throw new InvalidOperationException($"{first} decoded as {decoded.Data}, expected {data}");
            }

            return true;
        }).Check(config);
    }

    /// <summary>Record equality, with coordinates and altitudes compared to within rounding.</summary>
    private static bool SameIgnoringFloatingPoint(AprsData expected, AprsData actual)
    {
        if (expected is AprsPositionedData e && actual is AprsPositionedData a)
        {
            static bool Near(double? x, double? y) => (x is null && y is null) || (x is { } p && y is { } q && Math.Abs(p - q) <= 1e-9 * Math.Max(1, Math.Abs(p)));
            bool close = Near(e.Position.Latitude, a.Position.Latitude) && Near(e.Position.Longitude, a.Position.Longitude)
                && Near(e.AltitudeFeet, a.AltitudeFeet) && Near(e.SpeedKnots, a.SpeedKnots) && Near(e.RadioRangeMiles, a.RadioRangeMiles)
                && e.Position.Ambiguity == a.Position.Ambiguity;
            return close && a with { Position = e.Position, AltitudeFeet = e.AltitudeFeet, SpeedKnots = e.SpeedKnots, RadioRangeMiles = e.RadioRangeMiles } == e;
        }

        return expected == actual;
    }

    private static AprsPosition Masked(int latHundredths, int lonHundredths, bool south, bool west, int ambiguity)
    {
        // Mirror the decoder: blanked digits decode to the centre of their range.
        double Coordinate(int hundredths)
        {
            int deg = hundredths / 6000;
            double minutes = hundredths % 6000 / 100.0;
            double unit = ambiguity switch { 1 => 0.1, 2 => 1, 3 => 10, 4 => 60, _ => 0 };
            if (unit > 0)
            {
                minutes = (Math.Floor(Math.Round(minutes * 100) / Math.Round(unit * 100)) * unit) + (unit / 2);
            }

            return deg + (minutes / 60);
        }

        double lat = Coordinate(latHundredths);
        double lon = Coordinate(lonHundredths);
        return new AprsPosition(south ? -lat : lat, west ? -lon : lon, ambiguity);
    }
}
