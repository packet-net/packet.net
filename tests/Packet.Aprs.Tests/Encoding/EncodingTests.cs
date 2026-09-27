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
    public void Create_builds_the_whole_packet()
    {
        var status = new AprsStatusReport { Text = "Net Control" };
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", status, "WIDE1-1,WIDE2-1");
        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:>Net Control");
        packet.Diagnostics.Should().BeEmpty();
        AprsPacket.DecodeAx25(packet.ToAx25Frame()).Should().Be(packet);
    }
}
