using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsPositionDecoderTests
{
    // ─── Uncompressed format tests ─────────────────────────────────────
    //
    // Test cases lifted from the APRS-IS corpus (gb7rdg-node observation,
    // 2026-05-14) — each input is a real frame's info field, expected
    // lat/lon are direwolf's reference decode for the same frame.

    [Theory]
    [InlineData("!4725.22N/00810.83E_...", 47.42033333333333, 8.1805, '/', '_')]
    [InlineData("!4235.92NR07609.92W&WO4LFY-10 IGATE", 42.59866666666667, -76.16533333333334, 'R', '&')]
    [InlineData("!3038.84N/12034.99E#PHG", 30.647333333333332, 120.58316666666667, '/', '#')]
    [InlineData("!5410.60N/01800.26E#PHG5230FM Digi", 54.17666666666667, 18.004333333333335, '/', '#')]
    [InlineData("!3624.06NS13814.86E#PHG", 36.401, 138.24766666666667, 'S', '#')]
    [InlineData("!4459.80NR12258.46W&PHG0120OpenWebRX", 44.99666666666667, -122.97433333333333, 'R', '&')]
    public void TryDecode_Recovers_Uncompressed_Position_From_Real_Corpus_Frames(
        string info, double expectedLat, double expectedLon, char expectedTable, char expectedSymbol)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(info);

        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Format.Should().Be(AprsPositionFormat.Uncompressed);
        pos.Latitude.Should().BeApproximately(expectedLat, 1e-9);
        pos.Longitude.Should().BeApproximately(expectedLon, 1e-9);
        pos.SymbolTable.Should().Be(expectedTable);
        pos.SymbolCode.Should().Be(expectedSymbol);
    }

    [Fact]
    public void TryDecode_Extracts_Trailing_Comment_From_Uncompressed_Position()
    {
        var bytes = "!4725.22N/00810.83E_WX-MoerikenAG"u8;

        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Comment.Should().Be("WX-MoerikenAG");
    }

    [Fact]
    public void TryDecode_Handles_Empty_Comment()
    {
        var bytes = "!4725.22N/00810.83E_"u8;

        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Comment.Should().BeEmpty();
    }

    [Theory]
    [InlineData("=4725.22N/00810.83E_msg-capable")]
    [InlineData("!4725.22N/00810.83E_no-msg")]
    public void TryDecode_Strips_Leading_Dti_For_Bang_And_Equals(string info)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(info);
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.42033333333333, 1e-9);
    }

    // ─── Compressed format tests ───────────────────────────────────────
    //
    // Compressed example from corpus: !L3-3TM^`Ta  GSchneider i-Gate Batt=4.33V
    //   direwolf: lat=54.12600166666667, lon=-3.2305016666666666

    [Fact]
    public void TryDecode_Recovers_Compressed_Position_From_Real_Corpus_Frame()
    {
        var bytes = "!L3-3TM^`Ta  GSchneider i-Gate Batt=4.33V"u8;

        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Format.Should().Be(AprsPositionFormat.Compressed);
        pos.Latitude.Should().BeApproximately(54.12600166666667, 1e-6);
        pos.Longitude.Should().BeApproximately(-3.2305016666666666, 1e-6);
        pos.SymbolTable.Should().Be('L');
        pos.SymbolCode.Should().Be('a');
        pos.Comment.Should().Be("Schneider i-Gate Batt=4.33V");
    }

    // ─── Negative paths — rejections that match direwolf's error classes ──

    [Fact]
    public void TryDecode_Rejects_Bad_Hemisphere_Indicator()
    {
        // Last byte of lat field should be N or S — 'X' is invalid.
        var bytes = "!4725.22X/00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Invalid_Symbol_Table()
    {
        // '*' is not a valid symbol-table identifier.
        var bytes = "!4725.22N*00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Latitude_Minutes_Over_60()
    {
        // 75 minutes is invalid (minutes 0-59).
        var bytes = "!4775.22N/00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Longitude_Over_180_Degrees()
    {
        var bytes = "!4725.22N/19010.83E_"u8;  // 190° too far
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Compressed_With_Out_Of_Range_Base91_Char()
    {
        // '~' is 126, outside the base-91 range (! through {).
        var bytes = "!/3-3TM~`Ta  GTest"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Empty_Input()
    {
        AprsPositionDecoder.TryDecode(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Too_Short_Input()
    {
        AprsPositionDecoder.TryDecode("!short"u8, out _).Should().BeFalse();
    }

    // ─── Edge cases ────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_Handles_Equator_And_Prime_Meridian()
    {
        var bytes = "!0000.00N/00000.00E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().Be(0);
        pos.Longitude.Should().Be(0);
    }

    [Fact]
    public void TryDecode_Handles_Southern_Hemisphere_Negative_Longitude()
    {
        var bytes = "!3334.50S/05823.00W>"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeLessThan(0);
        pos.Latitude.Should().BeApproximately(-33.575, 1e-9);
        pos.Longitude.Should().BeApproximately(-58.383333333333, 1e-9);
    }

    [Fact]
    public void TryDecode_Accepts_Space_Padded_Minutes_Per_APRS12c_Privacy_Notation()
    {
        // APRS12c §8: a space character in any minute position indicates
        // "this digit is intentionally not transmitted for privacy".
        // Treated as zero per direwolf's convention.
        var bytes = "!47  .  N/00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.0, 1e-9);
    }

    // ─── Timestamped variants (DTI @ and /) ────────────────────────────

    [Fact]
    public void TryDecode_Strips_DHM_Zulu_Timestamp_From_At_Sign_Position()
    {
        // @ DTI + DHM zulu timestamp (DDHHMMz) + position.
        // 140951z = day 14, 09:51 zulu.
        var bytes = "@140951z4725.22N/00810.83E_WX"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.42033333333333, 1e-9);
        pos.Longitude.Should().BeApproximately(8.1805, 1e-9);
        pos.Comment.Should().Be("WX");
    }

    [Fact]
    public void TryDecode_Strips_DHM_Local_Timestamp_From_Slash_Position()
    {
        // / DTI + DHM local timestamp (terminator '/') + position.
        var bytes = "/140951/4725.22N/00810.83E_WX"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.42033333333333, 1e-9);
    }

    [Fact]
    public void TryDecode_Strips_HMS_Timestamp_From_At_Sign_Position()
    {
        // @ DTI + HMS timestamp (HHMMSSh terminator 'h') + position.
        var bytes = "@095123h4725.22N/00810.83E_WX"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.42033333333333, 1e-9);
    }

    [Fact]
    public void TryDecode_Strips_MDHM_8Byte_Timestamp()
    {
        // @ DTI + MDHM timestamp (8 digits, no terminator) + position.
        // 05140951 = May 14, 09:51.
        var bytes = "@051409514725.22N/00810.83E_WX"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Latitude.Should().BeApproximately(47.42033333333333, 1e-9);
    }

    [Fact]
    public void TryDecode_Handles_Timestamped_Compressed_Position()
    {
        // @ DTI + 7-byte DHM zulu + compressed position bytes.
        var bytes = "@140951zL3-3TM^`Ta  GBatt=4.33V"u8;
        AprsPositionDecoder.TryDecode(bytes, out var pos).Should().BeTrue();
        pos.Format.Should().Be(AprsPositionFormat.Compressed);
        pos.Latitude.Should().BeApproximately(54.12600166666667, 1e-6);
    }

    [Fact]
    public void TryDecode_Rejects_Timestamped_With_Malformed_Timestamp()
    {
        // Bad terminator byte (not z, /, h, or digit).
        var bytes = "@140951X4725.22N/00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_Timestamped_With_Non_Digit_In_Timestamp_Body()
    {
        var bytes = "@14X951z4725.22N/00810.83E_"u8;
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Rejects_At_Sign_DTI_With_Too_Short_Input()
    {
        var bytes = "@short"u8;  // not even 8 bytes for timestamp
        AprsPositionDecoder.TryDecode(bytes, out _).Should().BeFalse();
    }
}
