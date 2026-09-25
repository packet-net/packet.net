namespace Packet.Aprs.Tests.Encoding;

/// <summary>Building packets from data, and the encoder refusing anything APRS12c forbids.</summary>
public class EncodingTests : AprsSpec
{
    private static readonly AprsPosition Somewhere = new(49 + (3.50 / 60), -(72 + (1.75 / 60)));

    [Fact]
    public void A_simple_position_report()
    {
        var report = new AprsPositionReport
        {
            Position = Somewhere,
            Symbol = AprsSymbol.Parse("/-"),
            Comment = "Test 001234",
        };
        WhenEncoded(report);
        ThenEncodedIs("!4903.50N/07201.75W-Test 001234");
        ThenEncodedDecodesBackTo(report);
    }

    [Fact]
    public void Every_optional_element_in_canonical_order()
    {
        var report = new AprsPositionReport
        {
            Position = Somewhere,
            Symbol = AprsSymbol.Parse("/>"),
            MessagingCapable = true,
            Timestamp = AprsTimestamp.DayHoursMinutes(9, 23, 45),
            CourseDegrees = 88,
            SpeedKnots = 36,
            AltitudeFeet = 1234,
            Frequency = new AprsVoiceFrequency { FrequencyMHz = 146.52m, ToneType = AprsToneType.Tone, ToneValue = 100 },
            Comment = "Hello",
            Telemetry = new AprsCommentTelemetry { Sequence = 25, Analog = [470, 625] },
            Dao = AprsDao.Wgs84Base91,
        };
        WhenEncoded(report);
        ThenEncodedIs("@092345z4903.50N/07201.75W>088/036/A=001234146.520MHz T100 Hello|!:&0'p|!w!!!");
        ThenEncodedDecodesBackTo(report);
    }

    [Fact]
    public void Frequency_after_an_extension_is_separated_by_a_slash()
    {
        var report = new AprsPositionReport
        {
            Position = Somewhere,
            Symbol = AprsSymbol.Parse("/r"),
            Phg = new AprsPhg(5, 1, 3, 2),
            Frequency = new AprsVoiceFrequency { FrequencyMHz = 145.5m },
        };
        WhenEncoded(report);
        ThenEncodedIs("!4903.50N/07201.75WrPHG5132/145.500MHz");
        ThenEncodedDecodesBackTo(report);
    }

    [Fact]
    public void A_compressed_position_with_course_and_speed()
    {
        var report = new AprsPositionReport
        {
            Position = new AprsPosition(49.5, -72.75),
            Symbol = AprsSymbol.Parse("/>"),
            IsCompressed = true,
            CourseDegrees = 88,
            SpeedKnots = Math.Pow(1.08, 47) - 1,
            CompressionType = new AprsCompressionType(AprsGpsFix.Current, AprsNmeaSource.Rmc, AprsCompressionOrigin.Software),
            MessagingCapable = true,
        };
        WhenEncoded(report);

        // APRS12c §9's worked example truncates 190463 x 107.25 = 20427156.75 to "<*e7"; rounding to
        // the nearest step, as direwolf does, gives "<*e8" (docs/aprs-spec-interpretations.md).
        ThenEncodedIs("=/5L!!<*e8>7P[");
    }

    [Fact]
    public void An_object_with_weather()
    {
        var obj = new AprsObjectReport
        {
            Name = "BRENDA",
            Timestamp = AprsTimestamp.DayHoursMinutes(9, 23, 45),
            Position = Somewhere,
            Symbol = AprsSymbol.Parse("/_"),
            Weather = new AprsWeather { WindDirectionDegrees = 220, WindSpeedMph = 4, WindGustMph = 5, TemperatureFahrenheit = 77, HumidityPercent = 50, PressureMillibars = 990 },
        };
        WhenEncoded(obj);
        ThenEncodedIs(";BRENDA   *092345z4903.50N/07201.75W_220/004g005t077h50b09900");
        ThenEncodedDecodesBackTo(obj);
    }

    [Theory]
    [InlineData("123/456", "!4903.50N/07201.75W-/123/456")]
    [InlineData(" leading space", "!4903.50N/07201.75W-/ leading space")]
    [InlineData("/slash", "!4903.50N/07201.75W-//slash")]
    public void Comments_that_would_be_misread_are_protected_by_a_delimiter(string comment, string expected)
    {
        var report = new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Comment = comment };
        WhenEncoded(report);
        ThenEncodedIs(expected);
        ThenEncodedDecodesBackTo(report);
    }

    [Fact]
    public void A_message_with_reply_ack()
    {
        var msg = new AprsTextMessage { Addressee = "G3NRW", Text = "Hi Ian", MessageId = "01", ReplyAck = "" };
        WhenEncoded(msg);
        ThenEncodedIs(":G3NRW    :Hi Ian{01}");
        ThenEncodedDecodesBackTo(msg);
    }

    [Fact]
    public void Utf8_text_is_sent_as_utf8()
    {
        var status = new AprsStatusReport { Text = "73 de M0LTE °" };
        WhenEncoded(status);
        Encoded[^2..].Should().Equal(0xC2, 0xB0);
        ThenEncodedDecodesBackTo(status);
    }

    [Fact]
    public void Creating_a_packet_needs_mic_e_to_go_through_create_mic_e()
    {
        var mic = new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>") };
        FluentActions.Invoking(() => AprsPacket.Create("N0CALL", "APZ001", mic)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_builds_the_whole_packet()
    {
        var status = new AprsStatusReport { Text = "Net Control" };
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", status, "WIDE1-1,WIDE2-1");
        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:>Net Control");
        packet.Diagnostics.Should().BeEmpty();
        AprsPacket.DecodeAx25(packet.ToAx25Frame()).Should().Be(packet);
    }

    public static TheoryData<string, AprsData> Refusals => new()
    {
        { "message text with {", new AprsTextMessage { Addressee = "N0CALL", Text = "a{b" } },
        { "message text over 67 characters", new AprsTextMessage { Addressee = "N0CALL", Text = new string('x', 68) } },
        { "addressee over 9 characters", new AprsTextMessage { Addressee = "TOOLONGCALL", Text = "x" } },
        { "ack with its own message ID", new AprsMessageAck { Addressee = "N0CALL", AcknowledgedId = "1", MessageId = "2" } },
        { "object name over 9 characters", new AprsObjectReport { Name = "0123456789", Timestamp = AprsTimestamp.DayHoursMinutes(1, 0, 0), Position = Somewhere, Symbol = AprsSymbol.Parse("/-") } },
        { "object without timestamp", new AprsObjectReport { Name = "X", Position = Somewhere, Symbol = AprsSymbol.Parse("/-") } },
        { "item name too short", new AprsItemReport { Name = "AB", Position = Somewhere, Symbol = AprsSymbol.Parse("/-") } },
        { "weather report with a comment", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/_"), Weather = new AprsWeather(), Comment = "hello" } },
        { "weather without the weather symbol", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Weather = new AprsWeather() } },
        { "two data extensions", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Phg = new AprsPhg(1, 1, 1, 1), RadioRangeMiles = 5 } },
        { "PHG in a compressed report", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), IsCompressed = true, Phg = new AprsPhg(1, 1, 1, 1) } },
        { "ambiguity in a compressed report", new AprsPositionReport { Position = Somewhere with { Ambiguity = 2 }, Symbol = AprsSymbol.Parse("/-"), IsCompressed = true } },
        { "latitude beyond 90", new AprsPositionReport { Position = new AprsPosition(91, 0), Symbol = AprsSymbol.Parse("/-") } },
        { "invalid symbol table", new AprsPositionReport { Position = Somewhere, Symbol = new AprsSymbol('x', '-') } },
        { "comment that reads as altitude", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Comment = "at /A=001234" } },
        { "comment containing PHG text, which default decoding lifts out", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Comment = "PHG5132 lookalike" } },
        { "comment that reads as a DAO", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Comment = "x !W12!" } },
        { "comment with a line break", new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/-"), Comment = "a\rb" } },
        { "status with a non-DHM timestamp", new AprsStatusReport { Timestamp = AprsTimestamp.HoursMinutesSeconds(1, 2, 3), Text = "x" } },
        { "telemetry with 4 channels", new AprsTelemetryReport { Sequence = "001", Analog = [1, 2, 3, 4], Digital = 0 } },
        { "unknown Mic-E message type", new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>"), Message = AprsMicEMessage.Unknown } },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void The_encoder_refuses(string what, AprsData data)
    {
        _ = what;
        if (data is AprsMicEReport mic)
        {
            FluentActions.Invoking(() => AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), mic)).Should().Throw<ArgumentException>();
            return;
        }

        WhenEncodingIsAttempted(data);
        ThenEncodingFailed();
    }
}
