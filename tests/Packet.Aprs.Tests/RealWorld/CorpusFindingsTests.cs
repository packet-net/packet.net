namespace Packet.Aprs.Tests.RealWorld;

/// <summary>
/// Real packets from the 3,000,085-packet APRS-IS capture of 2026-09-26 that each exposed a gap
/// between what the decoder accepted cleanly and what the encoder would write back. The paired
/// strict / lenient cases for the flags added then are in <see cref="ToleranceTests"/>.
/// </summary>
public class CorpusFindingsTests : AprsSpec
{
    /// <summary>
    /// Text that would read as a frequency, altitude or the like if it came first is fine where
    /// the sender put it, and the encoder must be able to write it back there.
    /// </summary>
    [Theory]
    [InlineData("SQ9MDD-4>APBOX0,TCPIP*,qAC,T2WARSPL:=5215.02N/02055.60E-PHG2201 /145.575MHz lub SR5WZ")]
    [InlineData("OH2ABB-1>APDW17,TCPIP*,qAC,T2ROMANIA:!6024.22N/02540.71ErPHG5421 /A=000160 144.800MHz FX.25 Compatible RX")]
    [InlineData("OH2ABB-1>APDW17,TCPIP*,qAC,T2ROMANIA:;OH2RDN   *111111z6024.22N/02540.70ErPHG5441 /A=000160 144.625MHz VARA FM")]
    [InlineData("YO3IXW>APRX29,TCPIP*,qAC,T2ROM:;RoLink-Fo*111111z4541.87N/02711.02Er432.500MHz T103  R20k (Reteaua RoLink) http://www.439100.ro")]
    [InlineData("BH3NEK-3>TPUUU0,WIDE1-1,WIDE2-1,qAS,BH3NEK-10:`-T|l\"p>/\"8)}  144.640MHz 05.41V ")]
    public void Frequency_like_text_later_in_a_comment_is_written_back_where_it_was(string packet)
    {
        GivenPacket(packet);
        WhenDecoded();
        ThenNoProblems();
        ThenRoundTrips();
    }

    [Fact]
    public void Comment_text_that_would_change_meaning_is_still_refused()
    {
        // Written after the symbol, "/A=001234" is an altitude, so as comment text it can't be sent.
        var report = new AprsPositionReport { Position = new AprsPosition(49.0583, -72.0292), Symbol = AprsSymbol.Parse("/-"), Comment = "/A=001234" };
        WhenEncodingIsAttempted(report);
        ThenEncodingFailed();
    }

    /// <summary>Object names are any printable ASCII including spaces; only trailing spaces are padding (APRS12c ch. 11).</summary>
    [Theory]
    [InlineData("ON1TG-10>APMI06,TCPIP*,qAC,T2DENMARK:;   OR4F  *252047z5113.60N/00255.17EYMercator Oostende - ON4MO - OR4F", "   OR4F")]
    [InlineData("YU0UPI-S>APDG01,TCPIP*,qAC,YU0UPI-GS:;       B *252055z4309.81ND02221.41EaRNG0186/A=000098 70cm Voice (D-Star) 438.40000MHz -7.6000MHz", "       B")]
    public void An_object_name_may_start_with_spaces(string packet, string name)
    {
        GivenPacket(packet);
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().Name.Should().Be(name);
        ThenNoProblems();
        ThenRoundTrips();
    }

    [Fact]
    public void An_object_name_cannot_end_in_a_space_because_that_is_padding()
    {
        var obj = new AprsObjectReport { Name = "OR4F ", Timestamp = AprsTimestamp.DayHoursMinutes(1, 0, 0), Position = new AprsPosition(51, 2), Symbol = AprsSymbol.Parse("/-") };
        WhenEncodingIsAttempted(obj);
        ThenEncodingFailed();
    }

    /// <summary>A bot's help text that happens to start with a query type is not a query: the
    /// target of a directed query is one callsign.</summary>
    [Fact]
    public void Text_after_a_query_type_that_is_not_a_callsign_makes_a_plain_message()
    {
        GivenPacket("APRSPH>APAIOR,WIDE2-1,qAR,DU2XXR-2::KC5FM-9  :?APRSM for the last 10 direct msgs to you. U to leave the net /0926");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().Text.Should().StartWith("?APRSM for the last");
        ThenDiagnostic(AprsDiagnosticCode.InvalidQuery, AprsDiagnosticSeverity.Info);
        ThenNoProblems();
    }

    /// <summary>PARM. and UNIT. name 13 channels at most (5 analog, 8 digital, APRS12c ch. 13).</summary>
    [Fact]
    public void Units_for_more_than_13_channels_are_a_plain_message()
    {
        GivenInformationField(":MNARCH   :UNIT.Volt,Pkt,Pkt,Pcnt,None,On,On,On,On,Hi,Hi,Hi,Hi,Extra");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>();
        ThenDiagnostic(AprsDiagnosticCode.InvalidTelemetryMetadata, AprsDiagnosticSeverity.Info);
    }

    [Fact]
    public void The_encoder_refuses_names_for_more_than_13_channels_but_writes_15_coefficients()
    {
        WhenEncodingIsAttempted(new AprsTelemetryParameterNames { Addressee = "N0CALL", Names = [.. Enumerable.Range(1, 14).Select(i => $"C{i}")] });
        ThenEncodingFailed();

        WhenEncoded(new AprsTelemetryCoefficients { Addressee = "N0CALL", Coefficients = [.. Enumerable.Repeat(1m, 15)] });
        ThenEncodedIs(":N0CALL   :EQNS.1,1,1,1,1,1,1,1,1,1,1,1,1,1,1");
    }
}
