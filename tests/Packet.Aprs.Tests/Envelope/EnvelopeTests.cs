namespace Packet.Aprs.Tests.Envelope;

/// <summary>Headers: TNC2 text, AX.25 frames, paths and q-constructs.</summary>
public class EnvelopeTests : AprsSpec
{
    [Fact]
    public void Tnc2_header_with_digipeated_path()
    {
        GivenPacket("WB2OSZ>APDW18,N2GH,W2UB*,WIDE2-1:>hello");
        WhenDecoded();
        Packet.Source.Value.Should().Be("WB2OSZ");
        Packet.Destination.Value.Should().Be("APDW18");
        Packet.Path.Should().Equal(
            new AprsPathEntry(AprsAddress.Parse("N2GH"), true),
            new AprsPathEntry(AprsAddress.Parse("W2UB"), true),
            new AprsPathEntry(AprsAddress.Parse("WIDE2-1"), false));
        Packet.ToString().Should().Be("WB2OSZ>APDW18,N2GH,W2UB*,WIDE2-1:>hello");
    }

    [Fact]
    public void Tnc2_header_without_path()
    {
        GivenPacket("N0CALL>APZ001:>hi");
        WhenDecoded();
        Packet.Path.Should().BeEmpty();
        Packet.ToString().Should().Be("N0CALL>APZ001:>hi");
    }

    [Fact]
    public void Colons_after_the_first_belong_to_the_information_field()
    {
        GivenPacket("N0CALL>APZ001::N0CALL-1 :hi");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().Addressee.Should().Be("N0CALL-1");
    }

    [Theory]
    [InlineData("no header at all")]
    [InlineData(">APZ001:>hi")]
    [InlineData("N0CALL:>hi")]
    [InlineData("N0 CALL>APZ001:>hi")]
    public void An_unusable_header_throws(string line)
    {
        GivenPacket(line);
        WhenDecodingIsAttempted();
        ThenDecodingFailed();
        AprsPacket.TryDecode(line, out _).Should().BeFalse();
    }

    [Fact]
    public void Aprs_is_q_construct_and_igate()
    {
        GivenPacket("KB1QV-1>4SQP3U,WIDE1-1,WIDE2-1,qAR,N3LLO-2:`cK+l <0x1c>>/");
        WhenDecoded();
        Packet.QConstruct.Should().Be(new AprsQConstruct("qAR", AprsAddress.Parse("N3LLO-2"), 2));
        Packet.QConstruct!.Value.IsFromRf.Should().BeTrue();
    }

    [Fact]
    public void Aprs_is_names_need_not_be_ax25()
    {
        GivenPacket("N1KWG-10>APWW11,TCPIP*,qAC,T2SPAIN:=4301.96N/07123.43Wr");
        WhenDecoded();
        Packet.QConstruct!.Value.Station!.Value.IsAx25.Should().BeFalse();
        Packet.QConstruct!.Value.IsFromRf.Should().BeFalse();
    }

    [Theory]
    [InlineData("N2GH", true)]
    [InlineData("N2GH-1", true)]
    [InlineData("N2GH-15", true)]
    [InlineData("N2GH-0", false)]
    [InlineData("N2GH-16", false)]
    [InlineData("n2gh", false)]
    [InlineData("WHO-IS", false)]
    [InlineData("T2SPAIN", false)]
    [InlineData("N2GH-01", false)]
    public void Which_addresses_are_valid_on_air(string address, bool ax25) =>
        AprsAddress.Parse(address).IsAx25.Should().Be(ax25);

    [Fact]
    public void Same_station_ignores_a_zero_ssid_spelling()
    {
        AprsAddress.Parse("N0CALL").IsSameStation(AprsAddress.Parse("N0CALL-0")).Should().BeTrue();
        AprsAddress.Parse("N0CALL").Should().NotBe(AprsAddress.Parse("N0CALL-0"));
    }

    [Fact]
    public void Ax25_frame_round_trip()
    {
        AprsPacket original = AprsPacket.Decode("WB2OSZ-5>APDW18,N2GH*,WIDE2-1:!4237.14NS07120.83W#PHG7140");
        byte[] frame = original.ToAx25Frame();

        GivenBytes(frame);
        WhenDecodedAsAx25();

        Packet.Should().Be(original);
        Packet.Path[0].HasBeenRepeated.Should().BeTrue();
        Packet.Path[1].HasBeenRepeated.Should().BeFalse();
        ThenNoDiagnostics();
    }

    [Fact]
    public void Ax25_frame_bytes_follow_the_spec()
    {
        byte[] frame = AprsPacket.Decode("N0CALL-7>APZ001,WIDE1-1:>x").ToAx25Frame();

        // Destination: shifted "APZ001", then C bit set, reserved bits 11, SSID 0.
        frame[..6].Should().Equal("APZ001"u8.ToArray().Select(b => (byte)(b << 1)));
        frame[6].Should().Be(0b1110_0000);

        // Source: SSID 7, C bit clear, not last.
        frame[13].Should().Be(0b0110_1110);

        // Digipeater WIDE1-1, not yet repeated, last address.
        frame[20].Should().Be(0b0110_0011);
        frame[21].Should().Be(0x03);
        frame[22].Should().Be(0xF0);
        frame[23].Should().Be((byte)'>');
    }

    [Fact]
    public void Ax25_rejects_non_ui_frames()
    {
        byte[] frame = AprsPacket.Decode("N0CALL>APZ001:>x").ToAx25Frame();
        frame[14] = 0x3F; // SABM
        GivenBytes(frame);

        FluentActions.Invoking(WhenDecodedAsAx25).Should().Throw<AprsFormatException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == AprsDiagnosticCode.NotAprsFrame);
    }

    [Fact]
    public void Ax25_nul_padding_is_tolerated_by_default_and_rejected_when_strict()
    {
        byte[] frame = AprsPacket.Decode("K2CAT-1>APAT51:!4150.67N/07404.71W-").ToAx25Frame();
        frame[7 + 5] = 0; // UAP §5.29: "K2CAT" padded with NUL instead of a space
        GivenBytes(frame);

        WhenDecodedAsAx25();
        Packet.Source.Value.Should().Be("K2CAT-1");
        ThenToleratedWith(AprsDiagnosticCode.NulPaddedAddress);

        GivenStrictDecoding();
        FluentActions.Invoking(WhenDecodedAsAx25).Should().Throw<AprsFormatException>();
    }

    [Fact]
    public void Ax25_lower_case_address_is_tolerated_by_default_and_rejected_when_strict()
    {
        byte[] frame = AprsPacket.Decode("N2GH>APZ001:>x").ToAx25Frame();
        frame[7] = (byte)('n' << 1);
        GivenBytes(frame);

        WhenDecodedAsAx25();
        Packet.Source.Value.Should().Be("n2GH");
        ThenToleratedWith(AprsDiagnosticCode.InvalidAx25AddressCharacters);

        GivenStrictDecoding();
        FluentActions.Invoking(WhenDecodedAsAx25).Should().Throw<AprsFormatException>();
    }

    [Fact]
    public void Ax25_encoding_refuses_non_ax25_addresses()
    {
        AprsPacket packet = AprsPacket.Decode("WHO-IS>APJIW4,TCPIP*:>x");
        FluentActions.Invoking(packet.ToAx25Frame).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decoding_from_fields_you_already_have()
    {
        AprsPacket packet = AprsPacket.Decode(
            AprsAddress.Parse("M0LTE-9"),
            AprsAddress.Parse("APZ001"),
            AprsPathEntry.ParseList("WIDE1-1,WIDE2-1"),
            "!5130.00N/00007.00W>"u8);
        packet.Data.Should().BeOfType<AprsPositionReport>();
        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:!5130.00N/00007.00W>");
    }

    [Fact]
    public void Non_utf8_bytes_survive_in_the_raw_information_field()
    {
        GivenPacket("F1IQH>APU25N:>DX: 67<0xf8> 22:18");
        WhenDecoded();
        Packet.Information.ToArray()[^7].Should().Be(0xF8);
        Packet.ToTnc2()[^7].Should().Be(0xF8);
    }

    [Fact]
    public void Path_formatting_puts_a_single_star_after_the_last_used_entry()
    {
        IReadOnlyList<AprsPathEntry> path =
        [
            new(AprsAddress.Parse("N2GH"), true),
            new(AprsAddress.Parse("W2UB"), true),
            new(AprsAddress.Parse("WIDE2-1")),
        ];
        AprsPathEntry.FormatList(path).Should().Be("N2GH,W2UB*,WIDE2-1");
        AprsPathEntry.ParseList("N2GH,W2UB*,WIDE2-1").Should().Equal(path);
    }
}
