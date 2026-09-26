using System.Reflection;

namespace Packet.Aprs.Tests.RealWorld;

/// <summary>
/// Every <see cref="AprsParseOptions"/> tolerance, paired: <see cref="AprsParseOptions.Lenient"/> accepts the defect
/// with a warning, <see cref="AprsParseOptions.Strict"/> rejects it. Inputs are real packets from the APRS-IS corpus or
/// the Understanding APRS Packets error catalogue (UAP §5) wherever one exists.
/// </summary>
public class ToleranceTests : AprsSpec
{
    /// <summary>Flag name, the diagnostic it produces, a packet that needs it.</summary>
    public static TheoryData<string, AprsDiagnosticCode, string> Cases => new()
    {
        { nameof(AprsParseOptions.StripTrailingLineBreaks), AprsDiagnosticCode.TrailingLineBreak, "W1JT-7>APK102,AJ1L,W1MHL,N3LLO-3,WIDE2*::N1IQI    :ack84<0x0d>" },
        { nameof(AprsParseOptions.AllowNonUtf8Text), AprsDiagnosticCode.NonUtf8Text, "N3UJJ>APWW11,TCPIP*,qAC,WG3K-CA:>252043zDX: KV3B-2 14.4mi 313<0xb0> 20:23 3857.05N 07652.41W " },
        { nameof(AprsParseOptions.AllowLowercaseHemisphere), AprsDiagnosticCode.LowercaseHemisphere, "N1EOE>APN391,N1NCI-3*,WIDE2-1:!4216.95n/07243.20w#phg6230/ Easthampton MA" },
        { nameof(AprsParseOptions.AllowOutOfRangeValues), AprsDiagnosticCode.OutOfRangeValue, "N0CALL>APZ001:!4903.50N/07201.75W>400/020" },
        { nameof(AprsParseOptions.AllowObjectWithoutTimestamp), AprsDiagnosticCode.ObjectWithoutTimestamp, "N0CALL>APZ001:;FLIGHTPTH*4903.50N\\07201.75Wl610/310{100}" },
        { nameof(AprsParseOptions.AllowShortObjectName), AprsDiagnosticCode.ObjectNameNotPadded, "N0CALL>APZ001:;LEADER*092345z4903.50N/07201.75W>088/036" },
        { nameof(AprsParseOptions.AllowIncompleteWeather), AprsDiagnosticCode.IncompleteWeather, "N0CALL>APZ001:;BRENDA   *092345z4903.50N/07201.75W_220/004g005b09900" },
        { nameof(AprsParseOptions.AllowWeatherComment), AprsDiagnosticCode.WeatherComment, "W1TG2>APU25N,UNCAN*:@091842z4256.20N/07049.42W_310/004g015t081r000p033P002h54b10001/ - Hampton, NH Wx" },
        { nameof(AprsParseOptions.AllowKenwoodFfPadding), AprsDiagnosticCode.KenwoodFfPadding, "W1SHS-9>4R1X9U,W1MRA,WB2OSZ-5*:`c0<0x1d>mIL>/]\"4T}<0xff><0xff><0xff>=" },
        { nameof(AprsParseOptions.AllowEmptyDestination), AprsDiagnosticCode.EmptyDestination, "KB1EZZ-9>,W1IMD,UNCAN,WIDE2*:!4413.87N\\06936.24Wc205/041/A=000093EMA 902 COMMAND POST" },
        { nameof(AprsParseOptions.AllowEmptyPathEntry), AprsDiagnosticCode.EmptyPathEntry, "W1BKW-4>APNU19,:!4414.97NN06918.50W#PHG5730 W1BKW-4 Coggins Hill, Union, ME" },
        { nameof(AprsParseOptions.AllowMultipleUsedMarkers), AprsDiagnosticCode.MultipleUsedMarkers, "K2CAT-1>APAT51,K2RVW-1*,WIDE1*,WIDE2-2,qAR,N1ATP-12:!4150.67N/07404.71W-000/000/A=000000HVNY-73" },
        { nameof(AprsParseOptions.AllowMessageIdOnAck), AprsDiagnosticCode.MessageIdOnAck, "MPAD>APRS,qAR,X32DVA::KD4DRA-10:ack1348{4205" },
        { nameof(AprsParseOptions.AllowUnpaddedAddressee), AprsDiagnosticCode.UnpaddedAddressee, "N0CALL>APZ001::WU2Z:Testing" },
        { nameof(AprsParseOptions.AllowMissingSpaceAfterLocator), AprsDiagnosticCode.MissingSpaceAfterLocator, "KG5KTN-1>APWW11,W1WQM,WIDE1,N3LLO-3,WIDE2*:>FN42kw/-DX: KQ1L-8 28.7mi" },
        { nameof(AprsParseOptions.AllowIncompleteTelemetry), AprsDiagnosticCode.InvalidTelemetry, "DL1KHD-10>APLOX1,TCPIP*,qAC,T2SWEDEN:T#418,127" },
        { nameof(AprsParseOptions.AllowCompressionTypeReservedBits), AprsDiagnosticCode.CompressionTypeReservedBits, "SV4FHR-2>APU25N,TCPIP*,qAS,SV4FHR-1:@252042z/:L1pT/t)_&2bg007t063r000p000P000h79b10152" },
        { nameof(AprsParseOptions.AllowInvalidTimestamp), AprsDiagnosticCode.InvalidTimestamp, "OK2JCZ-6>APRS,TCPIP*,qAO,OK2JCZ:@204140z4922.06N/01607.14E_310/002g002t048r000p000P000h66b10226" },
        { nameof(AprsParseOptions.AllowNonStandardWeatherFieldWidths), AprsDiagnosticCode.NonStandardWeatherFieldWidth, "KC8RFE-2>APDW17,TCPIP*,qAC,T2RDU:!4019.38N/08808.63W_112/000g001t097r000p000P000h070b09982" },
        { nameof(AprsParseOptions.AllowWindFieldsInPositionWeather), AprsDiagnosticCode.WindFieldsInsteadOfExtension, "N8RJC-13>SKY,TCPIP*,qAC,T2DENMARK:@252040z3932.09N/08420.97W_c295s004g007t070r000P000h44b09999" },
        { nameof(AprsParseOptions.AllowWindExtensionAfterCompressed), AprsDiagnosticCode.WindExtensionAfterCompressed, "IZ8QHW-10>APLS01,TCPIP*,qAC,T2DENMARK:!L9ix<R5>$_!!G000/000g000t089P000p000h36b10203" },
        { nameof(AprsParseOptions.AllowDaoWithAmbiguity), AprsDiagnosticCode.DaoWithAmbiguity, "N0CALL>APZ001:!4903.  N/07201.  W-Hello!W12!" },
        { nameof(AprsParseOptions.AllowMalformedTimestamp), AprsDiagnosticCode.MalformedTimestamp, "OE3XXI>APMI06,WIDE2-1,qAR,OE3XTV:@252041_4810.14N/01637.23E&semiduplex 2/70 Gateway U=13.3V,T=23.6C" },
        { nameof(AprsParseOptions.AllowBraceInMessageText), AprsDiagnosticCode.BraceInMessageText, "NEX7>APK005R,N2EDX,qAR,AE7AF::OTA      :cq{" },
        { nameof(AprsParseOptions.AllowInvalidAddresseeCharacters), AprsDiagnosticCode.InvalidAddresseeCharacters, "W2DMB-7>APBTUV,WIDE1-1,WIDE2-1,qAR,W2DMB-10::QRX B-10 :ack78667" },
        { nameof(AprsParseOptions.AllowLetterGroupBulletin), AprsDiagnosticCode.LetterGroupBulletin, "K8SRR-10>APDW18,TCPIP*,qAS,K8SRR::BLNCNET  :E Panhandle Traffic Net @6:45pm ET on 147.255+ T123.0 09/25" },
        { nameof(AprsParseOptions.AllowFreeTextCapabilities), AprsDiagnosticCode.FreeTextCapabilities, "F5OHH>ID,qAR,F4GXS-3:<F5OHH PLX Digi v1.04 F5OHH Chris" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Lenient_options_accept_it_with_a_warning(string flag, AprsDiagnosticCode code, string packet)
    {
        _ = flag;
        GivenPacket(packet);
        WhenDecoded();
        ThenToleratedWith(code);
        Packet.Data.Should().NotBeOfType<AprsUnrecognizedData>();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Strict_options_reject_it(string flag, AprsDiagnosticCode code, string packet)
    {
        _ = flag;
        GivenPacket(packet);
        GivenStrictDecoding();
        WhenDecodingIsAttempted();
        ThenRejected(code);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Turning_off_only_that_flag_rejects_it(string flag, AprsDiagnosticCode code, string packet)
    {
        PropertyInfo property = typeof(AprsParseOptions).GetProperty(flag)!;
        var options = AprsParseOptions.Lenient with { };
        property.SetValue(options, false);
        GivenParseOptions(options);
        GivenPacket(packet);
        WhenDecodingIsAttempted();
        ThenRejected(code);
    }

    /// <summary>Header defects stop the whole packet (the decoder throws); information-field
    /// defects leave it undecoded with an error.</summary>
    private void ThenRejected(AprsDiagnosticCode code)
    {
        if (code is AprsDiagnosticCode.EmptyDestination or AprsDiagnosticCode.EmptyPathEntry or AprsDiagnosticCode.MultipleUsedMarkers)
        {
            ThenDecodingFailed();
            ThenFailureDiagnostic(code);
        }
        else
        {
            ThenRejectedWith(code);
        }
    }

    [Fact]
    public void Every_tolerance_flag_has_a_paired_test()
    {
        IEnumerable<string> flags = typeof(AprsParseOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(bool))
            .Select(p => p.Name)
            .Except([nameof(AprsParseOptions.RecognizeDataExtensionInComment), nameof(AprsParseOptions.AllowPositionNotAtStart), nameof(AprsParseOptions.AllowMicEAltitudeAnywhere)]) // interpretation flags, tested below
            .Except([nameof(AprsParseOptions.AllowNulPaddedAddress), nameof(AprsParseOptions.AllowInvalidAx25AddressCharacters)]); // AX.25 only, see Ax25FrameTests
        IEnumerable<string> tested = Cases.Select(row => (string)row[0]);
        flags.Should().BeSubsetOf(tested, "each new tolerance needs a strict-rejects / lenient-accepts pair (docs/strict-vs-pragmatic-audit.md)");
    }

    [Fact]
    public void Strict_turns_every_flag_off_and_lenient_turns_every_flag_on()
    {
        foreach (PropertyInfo p in typeof(AprsParseOptions).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            ((bool)p.GetValue(AprsParseOptions.Strict)!).Should().BeFalse(p.Name);
            ((bool)p.GetValue(AprsParseOptions.Lenient)!).Should().BeTrue(p.Name);
        }
    }

    [Fact]
    public void Phg_after_other_comment_text_is_recognised_by_default()
    {
        GivenPacket("UNCAN>APOT30:!4258.99N/07135.29W# 10.8V 98F PHG37306/ N1PA-Mt Uncanoonuc Digi");
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().Phg.Should().Be(new AprsPhg(3, 7, 3, 0));
        ThenToleratedWith(AprsDiagnosticCode.DataExtensionInComment);
    }

    [Fact]
    public void Phg_after_other_comment_text_stays_in_the_comment_when_strict()
    {
        GivenPacket("UNCAN>APOT30:!4258.99N/07135.29W# 10.8V 98F PHG37306/ N1PA-Mt Uncanoonuc Digi");
        GivenStrictDecoding();
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().Phg.Should().BeNull();
        ThenCommentIs("10.8V 98F PHG37306/ N1PA-Mt Uncanoonuc Digi");
        ThenNoDiagnostics();
    }

    [Fact]
    public void A_missing_timestamp_is_recognised_rather_than_misread()
    {
        // A '/' report with no timestamp. Skipping 7 characters, as FAP does, would decode
        // "N/08435.17W#McMinn" as a compressed position at 61.97N; the position starts right here.
        GivenPacket("KO4WHD-2>APMI0A,WIDE2-1,qAR,KG4FZR-3:/3517.73N/08435.17W#McMinn Co Digi");
        WhenDecoded();
        ThenPositionIs(35 + (17.73 / 60), -(84 + (35.17 / 60)));
        ThenToleratedWith(AprsDiagnosticCode.MalformedTimestamp);
        ThenCommentIs("McMinn Co Digi");
    }

    [Fact]
    public void An_object_with_a_garbled_timestamp_keeps_its_position()
    {
        // "ABCDEFz" also reads as a compressed position if the object is assumed to have no
        // timestamp; the real position follows the seven garbled characters.
        GivenInformationField(";TESTOBJ  *ABCDEFz5301.01N/01745.68Er");
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().Timestamp.Should().BeNull();
        ThenPositionIs(53 + (1.01 / 60), 17 + (45.68 / 60));
        ThenToleratedWith(AprsDiagnosticCode.MalformedTimestamp);
    }

    [Fact]
    public void An_object_with_a_garbled_timestamp_is_rejected_when_strict()
    {
        GivenInformationField(";TESTOBJ  *111111x5301.01N/01745.68Er");
        GivenStrictDecoding();
        WhenDecoded();
        ThenRejectedWith(AprsDiagnosticCode.MalformedTimestamp);
    }

    [Fact]
    public void A_position_after_other_text_is_found_by_default()
    {
        GivenPacket("RCHFLD>APN383,qAR,STRLNG:3832!3832.15NS11204.41W#PHG6738 W2,UTn-nN KB7K");
        WhenDecoded();
        ThenPositionIs(38 + (32.15 / 60), -(112 + (4.41 / 60)));
        ThenSymbolIs("S#");
        ThenToleratedWith(AprsDiagnosticCode.PositionNotAtStart);
    }

    [Fact]
    public void A_position_after_other_text_is_a_non_aprs_beacon_when_strict()
    {
        GivenPacket("RCHFLD>APN383,qAR,STRLNG:3832!3832.15NS11204.41W#PHG6738 W2,UTn-nN KB7K");
        GivenStrictDecoding();
        WhenDecoded();
        ThenUnrecognized(AprsUnrecognizedReason.NotAprs);
    }

    [Fact]
    public void Three_digit_humidity_is_read_whole_not_as_7_percent()
    {
        GivenPacket("KC8RFE-2>APDW17,TCPIP*,qAC,T2RDU:!4019.38N/08808.63W_112/000g001t097r000p000P000h070b09982");
        WhenDecoded();
        var wx = ThenDataIs<AprsPositionReport>().Weather!;
        wx.HumidityPercent.Should().Be(70);
        wx.PressureMillibars.Should().Be(998.2);
    }

    [Fact]
    public void Wind_fields_in_a_position_weather_report()
    {
        GivenPacket("N8RJC-13>SKY,TCPIP*,qAC,T2DENMARK:@252040z3932.09N/08420.97W_c295s004g007t070r000P000h44b09999");
        WhenDecoded();
        var wx = ThenDataIs<AprsPositionReport>().Weather!;
        wx.WindDirectionDegrees.Should().Be(295);
        wx.WindSpeedMph.Should().Be(4);
        wx.WindGustMph.Should().Be(7);
        wx.SnowfallLast24HoursInches.Should().BeNull();
    }

    [Fact]
    public void A_mic_e_altitude_at_the_end_is_found_by_default()
    {
        GivenPacket("KF0TXS-7>SXQR4V,WIDE1-1,WIDE2-2,qAR,KD0ZEA-6:`vF?l\"w[/WINLINK\"5t}");
        WhenDecoded();
        ThenAltitudeFeetIs(184 / 0.3048, tolerance: 0.01);
        ThenCommentIs("WINLINK");
        ThenToleratedWith(AprsDiagnosticCode.MicEAltitudeNotFirst);
    }

    [Fact]
    public void A_mic_e_altitude_at_the_end_stays_in_the_comment_when_strict()
    {
        GivenPacket("KF0TXS-7>SXQR4V,WIDE1-1,WIDE2-2,qAR,KD0ZEA-6:`vF?l\"w[/WINLINK\"5t}");
        GivenStrictDecoding();
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().AltitudeFeet.Should().BeNull();
        ThenCommentIs("WINLINK\"5t}");
    }

    [Fact]
    public void A_dao_shaped_run_inside_base91_telemetry_is_not_a_dao()
    {
        GivenInformationField("=3354.59N313448.96W:mobile|!m2n!Q[P|");
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.Dao.Should().BeNull();
        report.Telemetry!.Sequence.Should().Be(0 * 91 + ('m' - 33));
        ThenCommentIs("mobile");
        ThenReEncodesExactly();
    }
}
