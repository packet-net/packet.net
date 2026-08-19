using AwesomeAssertions;
using Packet.Tait.Codeplug;
using Xunit;

namespace Packet.Tait.Codeplug.Tests;

public class CodeplugFieldsTests
{
    private static CodeplugImage ImageWith(params CodeplugRecord[] records) =>
        new(new[] { new KeyValuePair<string, string>("DBVer", "0095") }, records);

    private static CodeplugRecord Channels(byte[] payload) => new(0x05, 0, payload);

    private static CodeplugRecord Data(byte[] payload) => new(0x09, 0, payload);

    private static CodeplugRecord Audio(byte[] payload) => new(0x3B, 0, payload);

    /// <summary>Place <paramref name="length"/> bits of <paramref name="value"/> (LSB first) at
    /// global bit <paramref name="bitOffset"/> in <paramref name="buf"/>.</summary>
    private static void PutBits(byte[] buf, int bitOffset, int length, long value)
    {
        for (int k = 0; k < length; k++)
        {
            if (((value >> k) & 1) != 0)
            {
                buf[(bitOffset + k) >> 3] |= (byte)(1 << ((bitOffset + k) & 7));
            }
        }
    }

    [Fact]
    public void Open_refuses_an_unmapped_database_version()
    {
        var image = new CodeplugImage(
            new[] { new KeyValuePair<string, string>("DBVer", "9999") },
            Array.Empty<CodeplugRecord>());
        Action act = () => CodeplugFields.Open(image);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Database_version_is_read_from_record_0x27_over_the_header()
    {
        // record 0x27 (12-bit field) says 0094; header says 0095. The record wins.
        var rec = new CodeplugRecord(0x27, 0, new byte[] { 0x5E, 0x00 }); // 0x05E = 94
        var image = new CodeplugImage(
            new[] { new KeyValuePair<string, string>("DBVer", "0095") },
            new[] { rec });
        image.DatabaseVersionFromRecord.Should().Be("0094");
        CodeplugFields.IsSupported(image).Should().BeTrue();
    }

    [Fact]
    public void Channel_fields_decode_at_their_pinned_bit_offsets()
    {
        // one channel = 181 bits -> 23 bytes.
        var ch = new byte[23];
        PutBits(ch, 0, 1, 1);                 // separate-tx bit 0
        PutBits(ch, 16, 32, 146_900_000);     // tx freq at bit 16
        PutBits(ch, 48, 32, 145_000_000);     // rx freq at bit 48
        PutBits(ch, 80, 2, (long)Bandwidth.Wide);   // bandwidth at bit 80
        PutBits(ch, 109, 3, (long)PowerLevel.High);  // power at bit 109

        CodeplugFields f = CodeplugFields.Open(ImageWith(Channels(ch)));
        f.ChannelCount.Should().Be(1);
        f.GetSeparateTxFrequency(0).Should().BeTrue();
        f.GetTxFrequencyHz(0).Should().Be(146_900_000);
        f.GetRxFrequencyHz(0).Should().Be(145_000_000);
        f.GetBandwidth(0).Should().Be(Bandwidth.Wide);
        f.GetPowerLevel(0).Should().Be(PowerLevel.High);
    }

    [Fact]
    public void Channel_fields_round_trip_through_set()
    {
        CodeplugFields f = CodeplugFields.Open(ImageWith(Channels(new byte[23])));
        f.SetTxFrequencyHz(0, 430_125_000);
        f.SetBandwidth(0, Bandwidth.Medium);
        f.SetPowerLevel(0, PowerLevel.Low);

        f.GetTxFrequencyHz(0).Should().Be(430_125_000);
        f.GetBandwidth(0).Should().Be(Bandwidth.Medium);
        f.GetPowerLevel(0).Should().Be(PowerLevel.Low);
    }

    [Fact]
    public void Channel_that_straddles_a_record_boundary_decodes_via_the_reassembled_stream()
    {
        // two channels = 362 bits -> 46 bytes, split into a 32-byte and a 14-byte record.
        // channel 1 starts at bit 181, so its later fields land in the second record.
        var full = new byte[46];
        PutBits(full, 181 + 16, 32, 435_000_000);          // ch1 tx freq
        PutBits(full, 181 + 80, 2, (long)Bandwidth.Wide);  // ch1 bandwidth (bit 261, second record)
        PutBits(full, 181 + 109, 3, (long)PowerLevel.Medium);

        var r0 = new CodeplugRecord(0x05, 0, full[..32]);
        var r1 = new CodeplugRecord(0x05, 1, full[32..]);
        CodeplugFields f = CodeplugFields.Open(ImageWith(r0, r1));

        f.ChannelCount.Should().Be(2);
        f.GetTxFrequencyHz(1).Should().Be(435_000_000);
        f.GetBandwidth(1).Should().Be(Bandwidth.Wide);
        f.GetPowerLevel(1).Should().Be(PowerLevel.Medium);
    }

    [Fact]
    public void Data_block_fields_use_their_pinned_bits()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[22])));
        f.SdmEnabled.Should().BeFalse();
        f.SdmEnabled = true;
        f.SdmEnabled.Should().BeTrue();
        (f.Image.Require(0x09, 0).Data[10] & 0x40).Should().Be(0x40);

        f.TransparentModeEnabled = true;
        f.TransparentModeEnabled.Should().BeTrue();

        f.DataPort = DataPort.InternalOptions;
        f.DataPort.Should().Be(DataPort.InternalOptions);

        f.FfskTransparentBaud = FfskBaud.Baud28800; // index 6 = 0b110, straddles [12] and [13]
        f.FfskTransparentBaud.Should().Be(FfskBaud.Baud28800);
    }

    [Fact]
    public void Audio_block_tap_fields_round_trip()
    {
        var f = CodeplugFields.Open(ImageWith(Audio(new byte[16])));
        f.SetRxTapOutNode(2);   // R2
        f.SetEptt1TapInNode(13); // T13
        f.TapOutUnmute = TapOutUnmute.ExceptOnPtt;

        f.GetRxTapOutNode().Should().Be(2);
        f.GetEptt1TapInNode().Should().Be(13);
        f.TapOutUnmute.Should().Be(TapOutUnmute.ExceptOnPtt);
        // the T-node marker bit is preserved
        (f.Image.Require(0x3B, 0).Data[11] & 0x20).Should().Be(0x20);
    }

    [Fact]
    public void Channel_subaudible_fields_decode_and_round_trip()
    {
        // pin the offsets: place TX type + RX type + indices, decode.
        var ch = new byte[23];
        PutBits(ch, 86, 2, (long)SubaudibleType.Ctcss); // TX type
        PutBits(ch, 88, 2, (long)SubaudibleType.Dcs);   // RX type
        PutBits(ch, 90, 8, 7);   // TX index
        PutBits(ch, 98, 8, 42);  // RX index

        CodeplugFields f = CodeplugFields.Open(ImageWith(Channels(ch)));
        f.GetTxSubaudibleType(0).Should().Be(SubaudibleType.Ctcss);
        f.GetRxSubaudibleType(0).Should().Be(SubaudibleType.Dcs);
        f.GetTxSubaudibleIndex(0).Should().Be(7);
        f.GetRxSubaudibleIndex(0).Should().Be(42);

        f.SetTxSubaudibleType(0, SubaudibleType.None);
        f.SetRxSubaudibleIndex(0, 5);
        f.GetTxSubaudibleType(0).Should().Be(SubaudibleType.None);
        f.GetRxSubaudibleIndex(0).Should().Be(5);
        // the RX type is untouched by setting the RX index
        f.GetRxSubaudibleType(0).Should().Be(SubaudibleType.Dcs);
    }

    [Fact]
    public void Subaudible_index_resolves_to_a_tone_via_the_codeplug_tables()
    {
        // channel 0: RX = CTCSS index 1, TX = DCS index 0.
        var ch = new byte[23];
        PutBits(ch, 88, 2, (long)SubaudibleType.Ctcss); // RX type
        PutBits(ch, 98, 8, 1);                          // RX index -> CTCSS table[1]
        PutBits(ch, 86, 2, (long)SubaudibleType.Dcs);   // TX type
        PutBits(ch, 90, 8, 0);                          // TX index -> DCS table[0]

        // CTCSS table (0x32): two 12-bit entries, freq*10 -> 67.0, 97.4.
        var ctcss = new byte[3];
        PutBits(ctcss, 0, 12, 670);
        PutBits(ctcss, 12, 12, 974);
        // DCS table (0x3D): one 9-bit entry, octal 017 = 15.
        var dcs = new byte[2];
        PutBits(dcs, 0, 9, 15);

        CodeplugFields f = CodeplugFields.Open(ImageWith(
            Channels(ch),
            new CodeplugRecord(0x32, 0, ctcss),
            new CodeplugRecord(0x3D, 0, dcs)));

        f.CtcssTable.Should().Equal(67.0, 97.4);
        f.DcsTable.Should().Equal("017");
        f.GetRxSubaudible(0).Should().Be("CTCSS 97.4");
        f.GetTxSubaudible(0).Should().Be("DCS 017");
    }

    [Fact]
    public void Audio_tap_inverted_bits_round_trip()
    {
        var f = CodeplugFields.Open(ImageWith(Audio(new byte[16])));
        f.RxTapOutInverted.Should().BeFalse();
        f.Eptt1TapInInverted.Should().BeFalse();

        f.RxTapOutInverted = true;
        f.Eptt1TapInInverted = true;
        (f.Image.Require(0x3B, 0).Data[4] & 0x40).Should().Be(0x40);
        (f.Image.Require(0x3B, 0).Data[14] & 0x08).Should().Be(0x08);

        f.RxTapOutInverted = false;
        f.RxTapOutInverted.Should().BeFalse();
        f.Eptt1TapInInverted.Should().BeTrue(); // independent
    }
}
