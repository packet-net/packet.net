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
    public void Channel_squelch_inhibit_network_decode_and_round_trip()
    {
        var ch = new byte[23];
        PutBits(ch, 82, 2, (long)TxInhibit.Mute);
        PutBits(ch, 84, 2, (long)Squelch.Hard);
        PutBits(ch, 106, 3, 5);

        CodeplugFields f = CodeplugFields.Open(ImageWith(Channels(ch)));
        f.GetTxInhibit(0).Should().Be(TxInhibit.Mute);
        f.GetSquelch(0).Should().Be(Squelch.Hard);
        f.GetNetwork(0).Should().Be(5);

        f.SetSquelch(0, Squelch.City);
        f.SetTxInhibit(0, TxInhibit.None);
        f.SetNetwork(0, 2);
        f.GetSquelch(0).Should().Be(Squelch.City);
        f.GetTxInhibit(0).Should().Be(TxInhibit.None);
        f.GetNetwork(0).Should().Be(2);
        ((Action)(() => f.SetNetwork(0, 8))).Should().Throw<ArgumentOutOfRangeException>();
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

        // Over-air FFSK modem rate: a 2-bit index in payload byte 21 bits[4:3] (fixture ffsk-baud-rate-1200).
        f.FfskModemBaud = FfskModemRate.Baud2400; // index 2 = 0b10
        f.FfskModemBaud.Should().Be(FfskModemRate.Baud2400);
        (f.Image.Require(0x09, 0).Data[21] & 0x18).Should().Be(0x10);
        f.FfskModemBaud = FfskModemRate.Baud1200; // index 0
        f.FfskModemBaud.Should().Be(FfskModemRate.Baud1200);
        (f.Image.Require(0x09, 0).Data[21] & 0x18).Should().Be(0x00);
    }

    [Fact]
    public void Ccdi_sdm_and_transparent_enablers_use_their_pinned_bits()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[37])));
        byte[] Payload() => f.Image.Require(0x09, 0).Data;

        // CCDI master (bit 177 = byte 22 bit 1) - gates RSSI / DCD / PTT / status.
        f.CcdiModeAllowed = true;
        f.CcdiModeAllowed.Should().BeTrue();
        (Payload()[22] & 0x02).Should().Be(0x02);

        // Power-up mode (bits 17..18 = byte 2 bits[2:1]); THSD Transparent = 2 = 0b10 -> bit 18 set.
        f.PowerupState = DataPowerupMode.ThsdTransparent; // index 2
        f.PowerupState.Should().Be(DataPowerupMode.ThsdTransparent);
        (Payload()[2] & 0x06).Should().Be(0x04);
        f.PowerupState = DataPowerupMode.FfskTransparent; // index 1 = 0b01 -> bit 17 set
        (Payload()[2] & 0x06).Should().Be(0x02);

        // CCDI command-mode baud (bits 97..99, straddles byte 12/13) and THSD baud (bits 107..109).
        f.CommandModeBaud = FfskBaud.Baud9600;
        f.CommandModeBaud.Should().Be(FfskBaud.Baud9600);
        f.HsdBaud = FfskBaud.Baud19200;
        f.HsdBaud.Should().Be(FfskBaud.Baud19200);

        // SDM output / progress / text sub-flags (bits 149, 151, 155..157, 170).
        f.CcdiSdmOutputEnabled = true; f.CcdiSdmOutputEnabled.Should().BeTrue();
        (Payload()[18] & 0x80).Should().Be(0x80);
        f.CcdiProgressMessageEnabled = true; f.CcdiProgressMessageEnabled.Should().BeTrue();
        (Payload()[18] & 0x20).Should().Be(0x20);
        f.TextSdmIndicator = true; f.TextSdmAutoAckTransmission = true; f.TextSdmAutoAckReception = true;
        (Payload()[19] & 0x38).Should().Be(0x38);
        f.CcdiSdmTextOnly = true; f.CcdiSdmTextOnly.Should().BeTrue();
        (Payload()[21] & 0x04).Should().Be(0x04);

        // SDM auto-ack numeric fields.
        f.SdmAutoAckDelayMs = 500; f.SdmAutoAckDelayMs.Should().Be(500); // 5 x 100 ms
        f.SdmWaitForAck = 6; f.SdmWaitForAck.Should().Be(6);

        // Transparent gotchas: ignore-escape (bit 114) and ignore-subaudible (bit 85).
        f.IgnoreEscapeSequence = true; f.IgnoreEscapeSequence.Should().BeTrue();
        (Payload()[14] & 0x04).Should().Be(0x04);
        f.IgnoreSubaudibleOnData = true; f.IgnoreSubaudibleOnData.Should().BeTrue();
        (Payload()[10] & 0x20).Should().Be(0x20);
    }

    [Fact]
    public void Sdm_auto_ack_numeric_fields_reject_out_of_range()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[37])));
        ((Action)(() => f.SdmAutoAckDelayMs = 5100)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.SdmAutoAckDelayMs = 150)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.SdmWaitForAck = 0)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.SdmWaitForAck = 16)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Remaining_data_form_fields_round_trip()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[32])));
        byte[] P() => f.Image.Require(0x09, 0).Data;

        // General
        f.OpenMonitorOnDialledCall = true; f.OpenMonitorOnDialledCall.Should().BeTrue();
        f.SelcallOutputEnabled = true; f.SelcallOutputEnabled.Should().BeTrue();
        f.MaximumInitialFrameLength = true; f.MaximumInitialFrameLength.Should().BeTrue();
        f.UartWriteDelayMs = 250; f.UartWriteDelayMs.Should().Be(250);
        f.TxBackoffTimeMinMs = 100; f.TxBackoffTimeMinMs.Should().Be(100);
        f.TxBackoffTimeMaxMs = 800; f.TxBackoffTimeMaxMs.Should().Be(800);

        // Serial: XON/XOFF are byte character codes (bits 1..8 / 9..16).
        f.XonCharacter = 0x11; f.XonCharacter.Should().Be(0x11);
        f.XoffCharacter = 0x13; f.XoffCharacter.Should().Be(0x13);
        f.CommandModeFlowControl = DataFlowControl.Software; f.CommandModeFlowControl.Should().Be(DataFlowControl.Software);
        f.FfskTransparentFlowControl = DataFlowControl.Hardware; f.FfskTransparentFlowControl.Should().Be(DataFlowControl.Hardware);
        f.HsdFlowControl = DataFlowControl.Software; f.HsdFlowControl.Should().Be(DataFlowControl.Software);

        // RF Modems
        f.CheckPacketLength = true; f.CheckPacketLength.Should().BeTrue();
        f.FfskToneBlanking = true; f.FfskToneBlanking.Should().BeTrue();
        f.FfskLeadInDelayMs = 500; f.FfskLeadInDelayMs.Should().Be(500); // 100 x 5 ms
        f.FfskLeadOutDelayMs = 200; f.FfskLeadOutDelayMs.Should().Be(200);
        f.WidebandModemEnabled = true; f.WidebandModemEnabled.Should().BeTrue();
        f.ThsdLayer2Protocol = ThsdLayer2.Total; f.ThsdLayer2Protocol.Should().Be(ThsdLayer2.Total);
        f.ThsdForwardErrorCorrection = true; f.ThsdForwardErrorCorrection.Should().BeTrue();
        f.ThsdNumberOfBlocks = 7; f.ThsdNumberOfBlocks.Should().Be(7);
        f.ThsdLeadInDelayMs = 5000; f.ThsdLeadInDelayMs.Should().Be(5000);
        f.ThsdLeadOutDelayMs = 250; f.ThsdLeadOutDelayMs.Should().Be(250);

        // SDM: the caller-ID checkbox drives both the encode (bit 152) and decode (bit 153) bits.
        f.SdmBufferOverwrite = true; f.SdmBufferOverwrite.Should().BeTrue();
        f.SdmCallerId = true; f.SdmCallerId.Should().BeTrue();
        (P()[19] & 0x03).Should().Be(0x03); // bits 152, 153 = byte 19 bits 0,1

        // TOTAL Transparent Mode
        f.TotalService = TotalModeService.Confirmed; f.TotalService.Should().Be(TotalModeService.Confirmed);
        f.TotalRadioId = 0x1234; f.TotalRadioId.Should().Be(0x1234);
        f.TotalSystemId = 0xAB; f.TotalSystemId.Should().Be(0xAB);
        f.TotalDestinationId = 0xFFFF; f.TotalDestinationId.Should().Be(0xFFFF);
        f.TotalLinkId = 0x5A; f.TotalLinkId.Should().Be(0x5A);
    }

    [Fact]
    public void Remaining_data_form_fields_reject_out_of_range()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[32])));
        ((Action)(() => f.FfskLeadInDelayMs = 3)).Should().Throw<ArgumentOutOfRangeException>();   // not a 5 ms step
        ((Action)(() => f.FfskLeadInDelayMs = 5105)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.ThsdNumberOfBlocks = 0)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.ThsdNumberOfBlocks = 8)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.TotalRadioId = 0x10000)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => f.TotalSystemId = 256)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Unit_data_identity_is_eight_char_ascii()
    {
        var f = CodeplugFields.Open(ImageWith(Data(new byte[37])));
        f.UnitDataIdentity.Should().BeEmpty();               // all-zero = blank
        f.UnitDataIdentity = "SOMEVALU";
        f.UnitDataIdentity.Should().Be("SOMEVALU");
        f.UnitDataIdentity = "NODE1";                        // shorter, trailing slots blank
        f.UnitDataIdentity.Should().Be("NODE1");
        f.UnitDataIdentity = "AB12*Z";                       // the wildcard is allowed
        f.UnitDataIdentity.Should().Be("AB12*Z");
        f.UnitDataIdentity = "";
        f.UnitDataIdentity.Should().BeEmpty();
        ((Action)(() => f.UnitDataIdentity = "TOOLONG12")).Should().Throw<ArgumentException>(); // > 8 chars
        ((Action)(() => f.UnitDataIdentity = "node1")).Should().Throw<ArgumentException>();      // lowercase
    }

    private static CodeplugFields GpsImage() => CodeplugFields.Open(ImageWith(
        new CodeplugRecord(0x05, 0, new byte[23]),   // one channel
        new CodeplugRecord(0x09, 0, new byte[37]),
        new CodeplugRecord(0x45, 0, new byte[21])));

    [Fact]
    public void Gps_fields_round_trip()
    {
        CodeplugFields f = GpsImage();
        f.HasGps.Should().BeTrue();

        f.SdmEnabled = true;                           // GPS enable requires SDM (below)
        f.GpsEnabled = true; f.GpsEnabled.Should().BeTrue();
        f.GpsSerialPort = DataPort.Aux; f.GpsSerialPort.Should().Be(DataPort.Aux);
        f.GpsBaudRate = FfskBaud.Baud14400; f.GpsBaudRate.Should().Be(FfskBaud.Baud14400);
        f.GpsPollResponseChannelType = GpsPollResponseChannelType.Dedicated;
        f.GpsPollResponseChannelType.Should().Be(GpsPollResponseChannelType.Dedicated);
        f.GpsPollResponseChannel = 1; f.GpsPollResponseChannel.Should().Be(1);
        f.GpsCalloutIntervalSeconds = 300; f.GpsCalloutIntervalSeconds.Should().Be(300);
        f.GpsMaxNumberOfCallouts = 5; f.GpsMaxNumberOfCallouts.Should().Be(5);
        f.GpsConnectionTimeoutSeconds = 600; f.GpsConnectionTimeoutSeconds.Should().Be(600);
        f.GpsLeadInDelayMs = 500; f.GpsLeadInDelayMs.Should().Be(500);
        f.GpsPollResponseDelayMs = 100; f.GpsPollResponseDelayMs.Should().Be(100);
        f.GpsSendOnEmergencyCallout = true; f.GpsSendOnEmergencyCallout.Should().BeTrue();
        f.GpsDispatcherAddress = "12345678"; f.GpsDispatcherAddress.Should().Be("12345678");

        ((Action)(() => f.GpsCalloutIntervalSeconds = 7)).Should().Throw<ArgumentOutOfRangeException>(); // not a 5 s step
        ((Action)(() => f.GpsConnectionTimeoutSeconds = 10)).Should().Throw<ArgumentOutOfRangeException>(); // below 20
    }

    [Fact]
    public void Gps_guards_match_the_cps()
    {
        CodeplugFields f = GpsImage();

        // GPS position reporting can only be enabled once SDM is on.
        ((Action)(() => f.GpsEnabled = true)).Should().Throw<InvalidOperationException>();
        f.SdmEnabled = true;
        f.GpsEnabled = true;

        // Dispatcher address is a radio identity (A-Z, 0-9, or '*'); letters are fine, other chars are not.
        f.GpsDispatcherAddress = "TESTADDR";
        f.GpsDispatcherAddress = "00099887";
        ((Action)(() => f.GpsDispatcherAddress = "test")).Should().Throw<ArgumentException>();     // lowercase
        ((Action)(() => f.GpsDispatcherAddress = "A-B")).Should().Throw<ArgumentException>();       // symbol

        // The GPS port takes any standard baud, including 28800.
        f.GpsBaudRate = FfskBaud.Baud28800; f.GpsBaudRate.Should().Be(FfskBaud.Baud28800);

        // The poll-response channel must be None (0) or an existing channel (this image has one).
        ((Action)(() => f.GpsPollResponseChannel = 5)).Should().Throw<ArgumentOutOfRangeException>();
        f.GpsPollResponseChannel = 1;
        f.GpsPollResponseChannel = 0;
    }

    [Fact]
    public void Customer_data_bytes_round_trip()
    {
        CodeplugFields f = CodeplugFields.Open(ImageWith(
            new CodeplugRecord(0x05, 0, new byte[23]),
            new CodeplugRecord(0x4C, 0, new byte[8]),
            new CodeplugRecord(0x4D, 0, new byte[4])));
        f.HasCustomerData.Should().BeTrue();

        for (int i = 1; i <= 4; i++) { f.SetCustomerGlobalByte(i, (byte)i); }
        for (int i = 1; i <= 4; i++) { f.GetCustomerGlobalByte(i).Should().Be((byte)i); }
        // global bytes live after four pad bytes
        f.Image.Require(0x4C, 0).Data[4].Should().Be(1);

        for (int i = 1; i <= 4; i++) { f.SetCustomerNetworkByte(1, i, (byte)(i + 4)); }
        for (int i = 1; i <= 4; i++) { f.GetCustomerNetworkByte(1, i).Should().Be((byte)(i + 4)); }
        f.Image.Require(0x4D, 0).Data[0].Should().Be(5);

        ((Action)(() => f.SetCustomerGlobalByte(5, 0))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Optional_blocks_report_absent()
    {
        CodeplugFields f = CodeplugFields.Open(ImageWith(new CodeplugRecord(0x05, 0, new byte[23])));
        f.HasGps.Should().BeFalse();
        f.HasCustomerData.Should().BeFalse();
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
    public void Setting_a_channel_tone_manages_the_table_and_item_counts()
    {
        // item index (record 0x01) with a CTCSS entry (item 0x32, 12-bit, count 1) and an empty
        // DCS entry (item 0x3D, 9-bit, count 0).
        var itemIndex = new byte[14];
        itemIndex[0] = 0x32; itemIndex[1] = 12; itemIndex[3] = 1; // CTCSS: count 1
        itemIndex[7] = 0x3D; itemIndex[8] = 9;                    // DCS: count 0
        CodeplugImage image = ImageWith(
            new CodeplugRecord(0x01, 0, itemIndex),
            new CodeplugRecord(0x05, 0, new byte[46]),  // 2 channels
            new CodeplugRecord(0x32, 0, new byte[2]));  // CTCSS table = [0] placeholder
        CodeplugFields f = CodeplugFields.Open(image);

        // Fill the free slot 0 (matches the CPS): table stays one entry, no count change.
        f.SetRxCtcss(0, 88.5);
        f.CtcssTable.Should().Equal(88.5);
        f.GetRxSubaudible(0).Should().Be("CTCSS 88.5");
        ItemCount(image, 0x32).Should().Be(1);

        // A distinct tone grows the table and bumps the item count.
        f.SetTxCtcss(1, 100.0);
        f.CtcssTable.Should().Equal(88.5, 100.0);
        f.GetTxSubaudible(1).Should().Be("CTCSS 100.0");
        ItemCount(image, 0x32).Should().Be(2);

        // DCS from an empty table creates the table record and takes the count 0 -> 1.
        f.SetRxDcs(1, "023");
        f.DcsTable.Should().Equal("023");
        f.GetRxSubaudible(1).Should().Be("DCS 023");
        ItemCount(image, 0x3D).Should().Be(1);

        f.SetRxSubaudibleNone(0);
        f.GetRxSubaudible(0).Should().Be("None");

        ((Action)(() => f.SetRxCtcss(0, 68.0))).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static int ItemCount(CodeplugImage image, byte itemId)
    {
        byte[] idx = image.Require(0x01, 0).Data;
        for (int off = 0; off + 7 <= idx.Length; off += 7)
        {
            if (idx[off] == itemId)
            {
                return idx[off + 3] | (idx[off + 4] << 8);
            }
        }

        return -1;
    }

    [Fact]
    public void Apply_packet_audio_defaults_writes_the_recommended_config()
    {
        // item index with a 0x3B (audio I/O) entry so the item count can be set.
        var itemIndex = new byte[7];
        itemIndex[0] = 0x3B; itemIndex[1] = 95;  // item 0x3B, recSizeBits 95, count 0
        CodeplugImage image = ImageWith(
            new CodeplugRecord(0x01, 0, itemIndex),
            new CodeplugRecord(0x3B, 0, new byte[8])); // some other prior audio record
        CodeplugFields f = CodeplugFields.Open(image);

        f.ApplyPacketAudioDefaults();

        // Byte-exact against the CPS's own "set to packet defaults" save.
        Convert.ToHexString(image.Require(0x3B, 0).Data).ToLowerInvariant()
            .Should().Be("000100c1088000004000803a0020004000001000");
        ItemCount(image, 0x3B).Should().Be(4);
        // The fields we can read match the recommended config.
        f.GetRxTapOutNode().Should().Be(1);        // Rx tap out R1
        f.GetEptt1TapInNode().Should().Be(13);     // EPTT1 tap in T13
        f.TapOutUnmute.Should().Be(TapOutUnmute.ExceptOnPtt);
        f.RxTapOutInverted.Should().BeFalse();
        f.Eptt1TapInInverted.Should().BeFalse();
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
