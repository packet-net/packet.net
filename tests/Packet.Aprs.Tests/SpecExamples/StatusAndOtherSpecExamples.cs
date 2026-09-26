namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Status (APRS12c §16), third-party (§17), frequency (§18), user-defined (§19), other (§20), DAO (§5).</summary>
public class StatusAndOtherSpecExamples : AprsSpec
{
    [Fact]
    public void Section16_status_without_timestamp()
    {
        GivenInformationField(">Net Control Center");
        WhenDecoded();
        var status = ThenDataIs<AprsStatusReport>();
        status.Text.Should().Be("Net Control Center");
        status.Timestamp.Should().BeNull();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section16_status_with_timestamp()
    {
        GivenInformationField(">092345zNet Control Center");
        WhenDecoded();
        ThenTimestampIs("092345z");
        ThenDataIs<AprsStatusReport>().Text.Should().Be("Net Control Center");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(">IO91SX/G", "IO91SX", "/G", "")]
    [InlineData(">IO91/G", "IO91", "/G", "")]
    [InlineData(">IO91SX/- My house", "IO91SX", "/-", "My house")]
    public void Section16_status_with_grid_locator(string info, string locator, string symbol, string text)
    {
        GivenInformationField(info);
        WhenDecoded();
        var status = ThenDataIs<AprsStatusReport>();
        status.MaidenheadLocator.Should().Be(locator);
        status.Symbol.ToString().Should().Be(symbol);
        status.Text.Should().Be(text);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section16_meteor_scatter_beam_heading_110_degrees_490_watts()
    {
        GivenInformationField(">IO91SX/- ^B7");
        WhenDecoded();
        var beam = ThenDataIs<AprsStatusReport>().BeamHeading!.Value;
        beam.HeadingDegrees.Should().Be(110);
        beam.ErpWatts.Should().Be(490);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section17_third_party_message_through_the_internet()
    {
        GivenPacket("G9RXG>APxxxx,WIDE2-2:}WB4APR-14>APxxxx,TCPIP,G9RXG*::G3NRW    :Hi Ian{001");
        WhenDecoded();
        var third = ThenDataIs<AprsThirdPartyTraffic>();
        third.CameFromInternet.Should().BeTrue();
        third.Packet.Source.Value.Should().Be("WB4APR-14");
        third.Packet.Path.Should().Equal(
            new AprsPathEntry(AprsAddress.Parse("TCPIP"), true),
            new AprsPathEntry(AprsAddress.Parse("G9RXG"), true));
        var msg = (AprsTextMessage)third.Packet.Data;
        msg.Addressee.Should().Be("G3NRW");
        msg.Text.Should().Be("Hi Ian");
        msg.MessageId.Should().Be("001");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section17_third_party_source_need_not_be_ax25()
    {
        GivenPacket("WB2OSZ-5>APDW17,WIDE1-1,WIDE2-1:}WHO-IS>APJIW4,TCPIP,WB2OSZ-5*::WB2OSZ-7 :ack0");
        WhenDecoded();
        var inner = ThenDataIs<AprsThirdPartyTraffic>().Packet;
        inner.Source.IsAx25.Should().BeFalse();
        inner.Data.Should().BeOfType<AprsMessageAck>().Which.AcknowledgedId.Should().Be("0");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section18_frequency_tone_and_offset_in_a_mic_e_comment()
    {
        GivenPacket("W1STJ-9>T2TU4Q,N1SFT,WIDE1,UNCAN,WIDE2*:`c8um^9j/`\"4I}146.685MHz T100 -060_1");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.Frequency.Should().Be(new AprsVoiceFrequency { FrequencyMHz = 146.685m, ToneType = AprsToneType.Tone, ToneValue = 100, OffsetKHz = -600 });
        mic.Device!.Model.Should().Be("FTM-300D");
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("146.52 MHz Enroute Alabama", 146.52, true, null, null, null, "Enroute Alabama")]
    [InlineData("147.105MHz AARC Radio Club", 147.105, false, null, null, null, "AARC Radio Club")]
    [InlineData("146.40 MHz T067 +100 Repeater", 146.40, true, AprsToneType.Tone, 67, 1000, "Repeater")]
    [InlineData("442.440MHz T107 -500 Repeater", 442.440, false, AprsToneType.Tone, 107, -5000, "Repeater")]
    [InlineData("145.50 MHz t077 Simplex", 145.50, true, AprsToneType.Tone, 77, null, "Simplex")]
    [InlineData("A96.000MHz microwave", 1296.000, false, null, null, null, "microwave")]
    public void Section18_frequency_formats_in_a_position_comment(string comment, double mhz, bool tenKhz, AprsToneType? tone, int? toneValue, int? offset, string rest)
    {
        GivenInformationField("!4903.50N/07201.75Wr" + comment);
        WhenDecoded();
        var f = ThenDataIs<AprsPositionReport>().Frequency!;
        f.FrequencyMHz.Should().Be((decimal)mhz);
        f.TenKilohertzResolution.Should().Be(tenKhz);
        f.ToneType.Should().Be(tone);
        f.ToneValue.Should().Be(toneValue);
        f.OffsetKHz.Should().Be(offset);
        ThenCommentIs(rest);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("{Q1qwerty", 'Q', '1', "qwerty")]
    [InlineData("{{zasdfg", '{', 'z', "asdfg")]
    public void Section19_user_defined_data(string info, char user, char type, string data)
    {
        GivenInformationField(info);
        WhenDecoded();
        var u = ThenDataIs<AprsUserDefinedData>();
        u.UserId.Should().Be(user);
        u.PacketType.Should().Be(type);
        u.DataText.Should().Be(data);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section20_invalid_or_test_data()
    {
        GivenInformationField(",191146,V,4214.2466,N,07303.5181,W,417.238,114.5,091099,14.7,W/GPS FIX");
        WhenDecoded();
        ThenDataIs<AprsTestData>().Data.Should().StartWith("191146,V");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section20_anything_else_is_a_non_aprs_beacon()
    {
        GivenPacket("W1IMD>BEACON,KQ1L-8,AB1OC-10,WIDE2*:W1IMD HIRAM, ME");
        WhenDecoded();
        ThenUnrecognized(AprsUnrecognizedReason.NotAprs);
        ThenDataIs<AprsUnrecognizedData>().Text.Should().Be("W1IMD HIRAM, ME");
        ThenDiagnostic(AprsDiagnosticCode.NotAprs, AprsDiagnosticSeverity.Info);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("!W23!", 49 + (3.502 / 60), -(72 + (1.753 / 60)), AprsDaoPrecision.Thousandths)]
    [InlineData("!wAb!", 49 + ((3.50 + (32 / 91.0 / 100)) / 60), -(72 + ((1.75 + (65 / 91.0 / 100)) / 60)), AprsDaoPrecision.Base91)]
    [InlineData("!W  !", 49 + (3.50 / 60), -(72 + (1.75 / 60)), AprsDaoPrecision.None)]
    public void Section5_dao_adds_precision(string dao, double lat, double lon, AprsDaoPrecision precision)
    {
        GivenInformationField("!4903.50N/07201.75W-" + dao);
        WhenDecoded();
        ThenPositionIs(lat, lon, tolerance: 1e-12);
        ThenDataIs<AprsPositionReport>().Dao.Should().Be(new AprsDao('W', precision));
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("[IO91SX] 35 miles NNW of London", "IO91SX", " 35 miles NNW of London")]
    [InlineData("[IO91]", "IO91", "")]
    public void Section8_maidenhead_beacon(string info, string locator, string comment)
    {
        GivenInformationField(info);
        WhenDecoded();
        var beacon = ThenDataIs<AprsMaidenheadBeacon>();
        beacon.Locator.Should().Be(locator);
        beacon.Comment.Should().Be(comment);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("$GPGGA,102705,5157.9762,N,00029.3256,W,1,04,2.0,75.7,M,47.6,M,,*62", 51 + (57.9762 / 60), -(29.3256 / 60))]
    [InlineData("$GPGLL,2554.459,N,08020.187,W,154027.281,A", 25 + (54.459 / 60), -(80 + (20.187 / 60)))]
    [InlineData("$GPRMC,063909,A,3349.4302,N,11700.3721,W,43.022,89.3,291099,13.6,E*52", 33 + (49.4302 / 60), -(117 + (0.3721 / 60)))]
    public void Section8_raw_nmea_positions(string info, double lat, double lon)
    {
        GivenInformationField(info);
        WhenDecoded();
        var nmea = ThenDataIs<AprsNmeaReport>();
        nmea.Position!.Value.Latitude.Should().BeApproximately(lat, 1e-9);
        nmea.Position!.Value.Longitude.Should().BeApproximately(lon, 1e-9);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("$GPGGA,102705,5157.9762,N,00029.3256,W,1,04,2.0,75.7,M,47.6,M,,*62", true)]
    [InlineData("$GPGLL,2554.459,N,08020.187,W,154027.281,A", false)]
    public void Section8_a_raw_nmea_checksum_is_optional(string info, bool hasChecksum)
    {
        GivenInformationField(info);
        WhenDecoded();
        ThenDataIs<AprsNmeaReport>().HasChecksum.Should().Be(hasChecksum);
    }

    /// <summary>
    /// Heard on APRS-IS: the longitude field has lost characters ("N8.9077"). NMEA 0183's checksum
    /// exists to catch exactly this, so the sentence is corrupt and nothing in it is trusted, in either
    /// mode (Ham::APRS::FAP rejects it too). Not a tolerance: the data can't be recovered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Section8_raw_nmea_with_a_checksum_that_does_not_match_is_corrupt(bool strict)
    {
        GivenPacket("N3EG-9>GPSMV,NICOLI,WIDE1,WICKI*,qAS,N3EG:$GPRMC,224137.000,A,4609.2815,N8.9077,W,29.11,125.01,250926,,,A*4B");
        if (strict)
        {
            GivenStrictDecoding();
        }

        WhenDecoded();
        ThenRejectedWith(AprsDiagnosticCode.NmeaChecksumMismatch);
    }

    [Fact]
    public void Section8_raw_nmea_course_and_speed()
    {
        GivenInformationField("$GPVTG,318.7,T,,M,35.1,N,65.0,K*69");
        WhenDecoded();
        var nmea = ThenDataIs<AprsNmeaReport>();
        nmea.CourseDegrees.Should().Be(318.7);
        nmea.SpeedKnots.Should().Be(35.1);
        nmea.SentenceType.Should().Be("GPVTG");
    }
}
