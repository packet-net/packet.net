namespace Packet.Aprs.Tests.Encoding;

/// <summary>
/// Building whole packets through the C# API. What the encoder writes for given data, and what it
/// refuses, is in the language-neutral vectors (<c>spec/aprs/cases/encode.json</c>).
/// </summary>
public class EncodingTests : AprsSpec
{
    private static readonly AprsPosition Somewhere = new(49 + (3.50 / 60), -(72 + (1.75 / 60)));

    [Fact]
    public void Creating_a_packet_needs_mic_e_to_go_through_create_mic_e()
    {
        var mic = new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>") };
        FluentActions.Invoking(() => AprsPacket.Create("N0CALL", "APZ001", mic)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_compression_type_with_no_cs_data_to_carry_it_is_refused()
    {
        // The unknown wind extension leaves the cs bytes blank, and blank cs bytes carry no T byte,
        // so writing them would lose the compression type (vectors interpretations.md, "Re-encoding
        // into compressed bytes rounds": a value the cs bytes cannot hold is refused).
        GivenInformationField("!/4_@>Q?Y`_.&H.../...g...t066");
        WhenDecoded();
        WhenEncodingIsAttempted(Packet.Data);
        ThenEncodingFailed();
    }

    [Fact]
    public void A_compressed_wind_direction_without_a_speed_is_refused()
    {
        // The cs bytes carry direction and speed together and cannot say either is unknown, so
        // writing this wind would turn the unknown speed into 0 (the mirror of the vectors case
        // weather/compressed-wind-speed-without-a-direction).
        GivenInformationField("!/5L!!<*e7_ sT156/...g000t066");
        WhenDecoded();
        WhenEncodingIsAttempted(Packet.Data);
        ThenEncodingFailed();
    }

    [Fact]
    public void A_mic_e_comment_that_would_end_an_altitude_is_refused()
    {
        // Written after the /A= altitude, the comment's "4} would read back as the Mic-E altitude 0"4}.
        GivenPacket("AK6ID-9>SS5UQQ:`.3 lSU>/`\"4}_01/A=000010 70cm%");
        WhenDecoded();
        var report = ThenDataIs<AprsMicEReport>();
        FluentActions.Invoking(() => AprsPacket.CreateMicE(Packet.Source, report)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_comment_after_an_nmea_sentence_needs_its_checksum()
    {
        var withChecksum = new AprsNmeaReport { Sentence = "GPGLL,2554.459,N,08020.187,W,154027.281,A*22", Comment = "/Home" };
        var without = withChecksum with { Sentence = "GPGLL,2554.459,N,08020.187,W,154027.281,A" };

        WhenEncodingIsAttempted(without);
        ThenEncodingFailed();

        WhenEncoded(withChecksum);
        ThenEncodedIs("$GPGLL,2554.459,N,08020.187,W,154027.281,A*22/Home");
        ThenEncodedDecodesBackTo(withChecksum with { Position = new AprsPosition(25.90765, -80.33645), FixValid = true, Time = new TimeOnly(15, 40, 27, 281), TimeText = "154027.281" });
    }

    [Fact]
    public void A_text_message_that_would_read_back_as_another_kind_is_refused()
    {
        // Addressed BLN1 it is a bulletin, and ?WX is a directed query (vectors README, Messages and queries).
        WhenEncodingIsAttempted(new AprsTextMessage { Addressee = "BLN1", Text = "Net tonight" });
        ThenEncodingFailed();
        WhenEncodingIsAttempted(new AprsTextMessage { Addressee = "N0CALL", Text = "?WX" });
        ThenEncodingFailed();
    }

    [Fact]
    public void A_weather_station_symbol_without_weather_is_refused()
    {
        // A decoder reads the _ symbol as a weather report, so this would read back as incomplete weather.
        WhenEncodingIsAttempted(new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.WeatherStation, Comment = "Hello" });
        ThenEncodingFailed();
    }

    [Fact]
    public void A_course_and_speed_after_the_area_symbol_is_refused()
    {
        // After \l, 088/036 reads as an area object (APRS12c ch. 11).
        WhenEncodingIsAttempted(new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.Parse("\\l"), CourseDegrees = 88, SpeedKnots = 36 });
        ThenEncodingFailed();
    }

    [Fact]
    public void An_ambiguous_position_at_the_pole_is_refused()
    {
        // The centre of the blanked box lies past 90 degrees, which a decoder rejects.
        WhenEncodingIsAttempted(new AprsPositionReport { Position = new AprsPosition(90, 0, 2), Symbol = AprsSymbol.Parse("/>") });
        ThenEncodingFailed();
    }

    [Fact]
    public void A_mic_e_course_of_0_is_refused()
    {
        // A decoder reads a Mic-E course of 0 as no course; north is 360.
        var report = new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>"), CourseDegrees = 0, SpeedKnots = 10 };
        FluentActions.Invoking(() => AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_mic_e_longitude_of_180_degrees_is_refused()
    {
        // Mic-E writes 100-109 degrees as 180-189 would be written, so 180 itself has no form (APRS12c ch. 10).
        var report = new AprsMicEReport { Position = new AprsPosition(49.05, 180), Symbol = AprsSymbol.Parse("/>") };
        FluentActions.Invoking(() => AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_mic_e_device_suffix_its_type_code_does_not_have_is_refused()
    {
        // |3 is not a suffix the device database lists after the > type code, so it would read
        // back as comment text.
        var report = new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>"), TypeCode = '>', DeviceSuffix = "|3", Comment = "Net" };
        FluentActions.Invoking(() => AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Mic_telemetry_with_an_empty_first_channel_keeps_its_comma()
    {
        var report = new AprsTelemetryReport { Sequence = "MIC", Analog = [null, 119m, 62.66m, 175m, 999m], Digital = 0b1001_0110 };

        WhenEncoded(report);
        ThenEncodedIs("T#MIC,,119,62.66,175,999,01101001");
        ThenEncodedDecodesBackTo(report);
    }

    [Fact]
    public void User_defined_data_ending_with_a_line_break_is_refused()
    {
        WhenEncodingIsAttempted(new AprsUserDefinedData { UserId = 'Q', PacketType = 'x', Data = "data\r"u8.ToArray() });
        ThenEncodingFailed();
    }

    [Fact]
    public void Snowfall_its_three_characters_cannot_hold_is_refused()
    {
        var weather = new AprsWeather { WindDirectionDegrees = 220, WindSpeedMph = 4, WindGustMph = 5, TemperatureFahrenheit = 77, SnowfallLast24HoursInches = 12.5m };
        WhenEncodingIsAttempted(new AprsPositionReport { Position = Somewhere, Symbol = AprsSymbol.WeatherStation, Weather = weather });
        ThenEncodingFailed();
    }

    [Fact]
    public void A_footprint_keeps_its_numbers_as_sent_but_compares_by_value()
    {
        GivenInformationField("?APRS? 34.360,-.1715,0200");
        WhenDecoded();
        var query = ThenDataIs<AprsGeneralQuery>();
        query.Footprint.Should().Be(new AprsQueryFootprint(34.36m, -0.1715m, 200));

        WhenEncoded(query);
        ThenEncodedIs("?APRS? 34.360,-.1715,0200");

        WhenEncoded(query with { Footprint = new AprsQueryFootprint(34.36m, -0.1715m, 200) });
        ThenEncodedIs("?APRS? 34.36,-0.1715,0200");
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
}
