using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsMicEDecoderTests
{
    [Fact]
    public void Decodes_Spec_Example_S32U6T()
    {
        // APRS101 §10 example: latitude 33°25.64′N, msg bits 1/0/0 → M3.
        // The info field bytes are constructed to give longitude 112.1°W,
        // speed 86 kn, course 251° (rough but spec-style example).
        // We don't have a clean §10 info-field example to lift verbatim,
        // so this test exercises the destination decode only.
        // (For full info-field round-trips see the corpus-sample tests below.)
        var dest = "S32U6T";
        // Build a minimal info field that satisfies all the range checks:
        //   d+28: long degrees 112 → d=12 (no offset), raw = 12+28 = 40 = '('
        //   m+28: 6 mins → raw = 28+6+60 = 94 (i.e. encode using high path)
        //   actually simplest: 12 minutes raw → 12+28=40 = '('
        //   h+28: 50 → raw 78 = 'N'
        //   SP+28: 8 → raw 36 (= '$') → speed_tens=8 → 80-89
        //   DC+28: 6*10+2 = 62 → raw 90 = 'Z' → units=6 course_h=2
        //   SE+28: 51 → raw 79 = 'O' → course_tu=51
        //   sym code: '>' (car)
        //   sym tbl: '/'
        var info = new byte[] { (byte)'`', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O', (byte)'>', (byte)'/' };
        AprsMicEDecoder.TryDecode(dest, info, out var r).Should().BeTrue();
        r.Latitude.Should().BeApproximately(33.4273, 1e-3);
        r.MessageType.Should().Be(MicEMessageType.StandardM3Returning);
        // dest byte 5 = 'T' → 'P'..'Z' range → West longitude
        r.Longitude.Should().BeLessThan(0);
        r.SymbolCode.Should().Be('>');
        r.SymbolTable.Should().Be('/');
    }

    [Theory]
    // Real corpus samples — destination + info bytes from the SQLite.
    // info_hex was captured for the first 12 bytes of the info field.
    //
    //   LA1SAR > U9UT82   info=60 26 3F 52 6C 21 76 5B 2F 22 34 4A  →  (59.913666, 10.592333)
    //   KC7OVS > TW4RXP   info=60 2C 50 47 6C 20 66 6B 2F 3E 22 3B  →  (47.713333, -116.873833)
    //   DO8RA-9 > TWTY61  info=60 28 33 1F 72 48 33 3E 2F 60 22 3A  →  (47.826833, 12.383833)
    [InlineData("U9UT82", "60263F526C21765B2F22344A", 59.913666, 10.592333)]
    [InlineData("TW4RXP", "602C50476C20666B2F3E223B", 47.713333, -116.873833)]
    [InlineData("TWTY61", "6028331F7248333E2F60223A", 47.826833,   12.383833)]
    public void Decodes_Real_Corpus_Samples(string dest, string infoHex, double expectedLat, double expectedLon)
    {
        var info = Convert.FromHexString(infoHex);
        AprsMicEDecoder.TryDecode(dest, info, out var r).Should().BeTrue();
        r.Latitude.Should().BeApproximately(expectedLat, 1e-3);
        r.Longitude.Should().BeApproximately(expectedLon, 1e-3);
    }

    [Fact]
    public void Decodes_Emergency_Code_When_All_Msg_Bits_Zero()
    {
        // Destination "234567" → all digits 0–9 → all msg bits 0 → emergency.
        // (lat 23.45.67 → 23°45.67′S, msg=Emergency)
        var info = new byte[] { (byte)'`', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O', (byte)'>', (byte)'/' };
        AprsMicEDecoder.TryDecode("234567", info, out var r).Should().BeTrue();
        r.MessageType.Should().Be(MicEMessageType.Emergency);
        r.Latitude.Should().BeApproximately(-23.7612, 1e-3);  // South (low chars on byte 3)
    }

    [Fact]
    public void Rejects_Wrong_Length_Destination()
    {
        var info = new byte[] { (byte)'`', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O', (byte)'>', (byte)'/' };
        AprsMicEDecoder.TryDecode("S32U6",  info, out _).Should().BeFalse();
        AprsMicEDecoder.TryDecode("S32U6TX", info, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_Invalid_Destination_Char()
    {
        var info = new byte[] { (byte)'`', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O', (byte)'>', (byte)'/' };
        // '!' is not a valid Mic-E destination character.
        AprsMicEDecoder.TryDecode("S32U6!", info, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_Too_Short_Info()
    {
        // Info must be at least 9 bytes (DTI + d + m + h + SP + DC + SE + symcode + symtbl).
        AprsMicEDecoder.TryDecode("S32U6T", new byte[] { (byte)'`', 1, 2 }, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_Wrong_DTI()
    {
        var info = new byte[] { (byte)'!', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O', (byte)'>', (byte)'/' };
        AprsMicEDecoder.TryDecode("S32U6T", info, out _).Should().BeFalse();
    }

    [Fact]
    public void Captures_Comment_After_Header()
    {
        // LA1SAR sample has the rest of the comment after byte 9.
        var info = Convert.FromHexString("60263F526C21765B2F22344A7D534F4F45");  // ...}SOOE
        AprsMicEDecoder.TryDecode("U9UT82", info, out var r).Should().BeTrue();
        r.Comment.Should().NotBeEmpty();
        r.Comment.Should().Be(@"""4J}SOOE");   // bytes 9+ inclusive (info[7..]=symcode then onward; we start at info[9])
    }
}
