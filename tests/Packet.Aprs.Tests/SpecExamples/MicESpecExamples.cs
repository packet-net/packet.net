namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Mic-E: APRS12c §10 worked examples, and real packets from Understanding APRS Packets §2.2.</summary>
public class MicESpecExamples : AprsSpec
{
    [Fact]
    public void Section10_destination_S32U6T_is_33_25_64_north_west_returning()
    {
        GivenPacket("N0CALL>S32U6T:`(_fn\"Oj/");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        PositionOf(mic).Latitude.Should().BeApproximately(33 + (25.64 / 60), 1e-9);
        mic.Message.Should().Be(AprsMicEMessage.Returning);
        mic.Position.Longitude.Should().BeNegative();
    }

    [Fact]
    public void Section10_information_field_decodes_to_112_07_74_west_20_knots_course_251_jeep()
    {
        // The worked example assumes a destination with longitude offset +100 and West.
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/");
        WhenDecoded();
        ThenPositionIs(33 + (25.64 / 60), -(112 + (7.74 / 60)));
        ThenCourseAndSpeedAre(251, 20);
        ThenSymbolIs("/j");
        var mic = ThenDataIs<AprsMicEReport>();
        mic.IsCurrent.Should().BeTrue();
        mic.TypeCode.Should().BeNull();
        ThenNoDiagnostics();
        ThenReEncodesExactly();
        Packet.Destination.Value.Should().Be("S32UVT");
    }

    [Theory]
    [InlineData("S32U6T", AprsMicEMessage.Returning)]
    [InlineData("F2DU6T", AprsMicEMessage.Custom2)]
    [InlineData("234U6T", AprsMicEMessage.Emergency)]
    public void Section10_position_comment_bits(string destination, AprsMicEMessage message)
    {
        GivenPacket($"N0CALL>{destination}:`(_fn\"Oj/");
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().Message.Should().Be(message);
    }

    [Fact]
    public void Section10_ambiguity_in_the_destination_applies_to_the_longitude()
    {
        GivenPacket("N0CALL>T4SQZZ:`(_fn\"Oj/");
        WhenDecoded();
        ThenPositionIs(44 + (31.5 / 60), -(112 + (7.5 / 60)), ambiguity: 2);
        ThenDataIs<AprsMicEReport>().Message.Should().Be(AprsMicEMessage.InService);
        ThenRoundTrips();
    }

    [Fact]
    public void Section10_altitude_in_status_text_is_metres_above_minus_10km()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`\"4T}");
        WhenDecoded();
        ThenAltitudeFeetIs(200.13, tolerance: 0.01);
        ThenDataIs<AprsMicEReport>().TypeCode.Should().Be('`');
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Understanding_2_2_yaesu_with_altitude_suffix_and_trailing_cr()
    {
        GivenPacket("N1JCM-9>TRQP7T,WA1PLE-4*:`c'wl|+>/`\"4-}_%<0x0d>");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.TypeCode.Should().Be('`');
        mic.DeviceSuffix.Should().Be("_%");
        mic.Device!.ToString().Should().Be("Yaesu FTM-400DR");
        mic.MessagingCapable.Should().BeTrue();
        ThenSymbolIs("/>");
        ThenCommentIs("");
        ThenToleratedWith(AprsDiagnosticCode.TrailingLineBreak);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Understanding_2_2_unprintable_bytes_and_altitude_without_type_code()
    {
        GivenPacket("N1YOQ-1>TRUW5X,UNCAN*,WIDE2-1:`c9r<0x1c><0x1f>;#/\"5D}Solar Powered Digipeter");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.TypeCode.Should().BeNull();
        ThenSymbolIs("/#");
        ThenCommentIs("Solar Powered Digipeter");
        ThenDiagnostic(AprsDiagnosticCode.MicEMissingDeviceType, AprsDiagnosticSeverity.Info);

        // Speed and course have two valid encodings (APRS12c §10); this sender used the one with
        // control characters and the encoder writes the printable one, so compare decoded values.
        ThenRoundTrips();
    }

    /// <summary>Every optional Mic-E element at once; expected values cross-checked with Ham::APRS::FAP.</summary>
    [Fact]
    public void Understanding_3_5_aircraft_with_altitude_telemetry_dao_and_tinytrak_suffix()
    {
        GivenPacket("N83MZ>T2TQ5U,WA1PLE-4*:`c.l+@&'/'\"G:} KJ6TMS|!:&0'p|!w#f!|3");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        ThenPositionIs(42.6925036630037, -71.3134597069597, tolerance: 1e-9);
        ThenCourseAndSpeedAre(210, 153);
        ThenSymbolIs("/'");
        ThenAltitudeFeetIs(1764 / 0.3048, tolerance: 0.01);
        mic.Message.Should().Be(AprsMicEMessage.InService);
        mic.TypeCode.Should().Be('\'');
        mic.MessagingCapable.Should().BeFalse();
        mic.DeviceSuffix.Should().Be("|3");
        mic.Device!.Model.Should().Be("TinyTrak3");
        mic.Telemetry!.Sequence.Should().Be(25);
        mic.Telemetry.Analog.Should().Equal(470, 625);
        mic.Dao.Should().Be(new AprsDao('W', AprsDaoPrecision.Base91));
        ThenCommentIs("KJ6TMS"); // the leading space is a delimiter (APRS12c §18); direwolf and FAP agree
        ThenNoProblems();
        ThenRoundTrips();
    }

    [Theory]
    [InlineData(">", "", "Kenwood TH-D7A")]
    [InlineData(">", "=", "Kenwood TH-D72")]
    [InlineData(">", "^", "Kenwood TH-D74")]
    [InlineData("]", "", "Kenwood TM-D700")]
    [InlineData("]", "=", "Kenwood TM-D710")]
    [InlineData("`", "_ ", "Yaesu VX-8")]
    [InlineData("`", "_$", "Yaesu FT1D")]
    [InlineData("'", "|4", "Byonics TinyTrak4")]
    public void Section10_device_type_codes_identify_the_radio(string prefix, string suffix, string device)
    {
        GivenPacket($"N1ZZN-9>T2SP0W:`c_Vm6hk/{prefix}\"49}}Test{suffix}");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.Device!.ToString().Should().Be(device);
        mic.DeviceSuffix.Should().Be(suffix);
        ThenCommentIs("Test");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Understanding_5_10_kenwood_ff_padding_is_removed_before_the_suffix()
    {
        GivenPacket("W1SHS-9>4R1X9U,W1MRA,WB2OSZ-5*:`c0<0x1d>mIL>/]\"4T}<0xff><0xff><0xff><0xff>=<0x0d>");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.Device!.Model.Should().Be("TM-D710");
        ThenCommentIs("");
        ThenToleratedWith(AprsDiagnosticCode.KenwoodFfPadding);
    }

    [Fact]
    public void Understanding_5_10_kenwood_ff_padding_is_rejected_when_strict()
    {
        GivenPacket("W1SHS-9>4R1X9U:`c0<0x1d>mIL>/]\"4T}<0xff><0xff>=");
        GivenStrictDecoding();
        WhenDecoded();
        ThenRejectedWith(AprsDiagnosticCode.KenwoodFfPadding);
    }

    [Fact]
    public void Encoding_a_mic_e_report_computes_the_destination()
    {
        var report = new AprsMicEReport
        {
            Position = new AprsPosition(33 + (25.64 / 60), -(112 + (7.74 / 60))),
            Symbol = new AprsSymbol('/', 'j'),
            CourseDegrees = 251,
            SpeedKnots = 20,
            Message = AprsMicEMessage.Returning,
        };

        AprsPacket packet = AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report);

        packet.Destination.Value.Should().Be("S32UVT");
        AprsPacket.Decode(packet.ToTnc2()).Data.Should().Be(report);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(">", '>')]
    [InlineData("]", ']')]
    public void Section10_maidenhead_locator_in_the_status_text(string prefix, char? typeCode)
    {
        // "IO91SX/G Helloworld" from a Mic-E or PIC-E, ">IO91SX/G ..." from a TH-D7, "]IO91SX/G ..." from a TM-D700.
        GivenPacket($"N0CALL>S32UVT:`(_fn\"Oj/{prefix}IO91SX/G Helloworld");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.MaidenheadLocator.Should().Be("IO91SX");
        mic.TypeCode.Should().Be(typeCode);
        ThenCommentIs("Helloworld");
        ThenNoProblems();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section10_four_character_locator_alone()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`IO91/G");
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().MaidenheadLocator.Should().Be("IO91");
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section10_locator_after_altitude()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`\"4T}IO91SX/G Hello");
        WhenDecoded();
        var mic = ThenDataIs<AprsMicEReport>();
        mic.MaidenheadLocator.Should().Be("IO91SX");
        ThenAltitudeFeetIs(200.13, tolerance: 0.01);
        ThenCommentIs("Hello");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section10_locator_letters_may_be_received_in_lower_case()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`io91sx/G Hello");
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().MaidenheadLocator.Should().Be("IO91SX");
        ThenNoProblems();
        ThenRoundTrips();
    }

    [Fact]
    public void Section10_text_straight_after_the_locator_is_tolerated_by_default()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`IO91SX/GHello");
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().MaidenheadLocator.Should().Be("IO91SX");
        ThenCommentIs("Hello");
        ThenToleratedWith(AprsDiagnosticCode.MissingSpaceAfterLocator);
    }

    [Fact]
    public void Section10_text_straight_after_the_locator_is_rejected_when_strict()
    {
        GivenPacket("N0CALL>S32UVT:`(_fn\"Oj/`IO91SX/GHello");
        GivenStrictDecoding();
        WhenDecoded();
        ThenRejectedWith(AprsDiagnosticCode.MissingSpaceAfterLocator);
    }

    [Fact]
    public void Encoding_a_locator_puts_a_space_before_the_comment()
    {
        var report = new AprsMicEReport
        {
            Position = new AprsPosition(51.5, -0.1),
            Symbol = new AprsSymbol('/', '>'),
            SpeedKnots = 0,
            TypeCode = '`',
            MaidenheadLocator = "IO91WM",
            Comment = "Hello",
        };

        AprsPacket packet = AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report);

        System.Text.Encoding.ASCII.GetString(packet.Information.Span[9..]).Should().Be("`IO91WM/G Hello");
        AprsPacket.Decode(packet.ToTnc2()).Data.Should().Be(report);
    }

    [Fact]
    public void A_comment_that_only_looks_like_a_locator_is_protected_by_a_delimiter()
    {
        var report = new AprsMicEReport
        {
            Position = new AprsPosition(51.5, -0.1),
            Symbol = new AprsSymbol('/', '>'),
            SpeedKnots = 0,
            TypeCode = '`',
            Comment = "IO91SX/G is where I am",
        };

        AprsPacket packet = AprsPacket.CreateMicE(AprsAddress.Parse("N0CALL"), report);

        System.Text.Encoding.ASCII.GetString(packet.Information.Span[9..]).Should().Be("`/IO91SX/G is where I am");
        AprsPacket.Decode(packet.ToTnc2()).Data.Should().Be(report);
    }
}
