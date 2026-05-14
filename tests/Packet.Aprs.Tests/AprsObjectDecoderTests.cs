using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsObjectDecoderTests
{
    // Real corpus samples (info-field bytes after the DTI). Lat/lon are
    // direwolf's decoded reference values to within ~1e-4° (uncompressed
    // APRS positions have ~18 m best-case resolution).

    [Theory]
    // Repeater object, uncompressed, primary symbol table, `r` (repeater).
    [InlineData(";439.137JJ*111111z5301.01N/01745.68ErC074 -760 SR2GG*Szubin-KPSR",
                "439.137JJ", true,  53.016833, 17.761333, '/', 'r')]
    // Killed object (rare in the wild but spec-supported).
    [InlineData(";KILLED   _111111z5301.01N/01745.68E_",
                "KILLED   ", false, 53.016833, 17.761333, '/', '_')]
    // Object with trailing spaces in the name (fixed-width preservation).
    [InlineData(";W4CRL  B *120017z2545.60ND08011.40WaRNG0020 440 Voice",
                "W4CRL  B ", true,  25.760000, -80.190000, 'D', 'a')]
    public void Decodes_Uncompressed_Object(
        string infoText, string expectedName, bool expectedAlive,
        double expectedLat, double expectedLon,
        char expectedSymTable, char expectedSymCode)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsObjectDecoder.TryDecode(info, out var obj).Should().BeTrue();
        obj.Name.Should().Be(expectedName);
        obj.IsAlive.Should().Be(expectedAlive);
        obj.Position.Latitude.Should().BeApproximately(expectedLat, 1e-4);
        obj.Position.Longitude.Should().BeApproximately(expectedLon, 1e-4);
        obj.Position.SymbolTable.Should().Be(expectedSymTable);
        obj.Position.SymbolCode.Should().Be(expectedSymCode);
        obj.Position.Format.Should().Be(AprsPositionFormat.Uncompressed);
    }

    [Fact]
    public void Accepts_Local_Time_Slash_Terminator()
    {
        // Spec §11 allows '/' (DHM local) as well as 'z' (DHM zulu).
        var info = System.Text.Encoding.ASCII.GetBytes(
            ";TESTOBJ  *111111/5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(info, out var obj).Should().BeTrue();
        obj.Name.Should().Be("TESTOBJ  ");
        obj.IsAlive.Should().BeTrue();
    }

    [Fact]
    public void Rejects_Missing_Alive_Killed_Indicator()
    {
        // The 11th character must be '*' or '_'. Other characters fail.
        var info = System.Text.Encoding.ASCII.GetBytes(
            ";BADOBJ   X111111z5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_Non_Numeric_Timestamp()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(
            ";TESTOBJ  *ABCDEFz5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Fact]
    public void Accepts_HMS_Timestamp_Per_Spec()
    {
        // APRS101 §11 lists three valid 7-byte timestamps for objects:
        // DDHHMMz, DDHHMM/, HHMMSSh. We must accept all three on receive.
        var info = System.Text.Encoding.ASCII.GetBytes(
            ";TESTOBJ  *123456h5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(info, out var obj).Should().BeTrue();
        obj.Name.Should().Be("TESTOBJ  ");
    }

    [Fact]
    public void Rejects_Invalid_Timestamp_Terminator()
    {
        // 'x' isn't one of z / / h.
        var info = System.Text.Encoding.ASCII.GetBytes(
            ";TESTOBJ  *111111x5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_Too_Short_Input()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(";TOOSHORT");
        AprsObjectDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Theory]
    // Real corpus samples with compressed (base-91) position payloads.
    // Reference lat/lon values match direwolf's decode.
    [InlineData(";443.500IA*140952z/9K=V6lyur   Brandmeister DMR 3119 TS2",
                "443.500IA", true,  41.6019,  -93.6097, '/', 'r')]
    [InlineData(";GB3GH    *140954z/4:#BMv{+r  CRB:05 433.125MHz CTCSS:118.8",
                "GB3GH    ", true,  51.8690,   -2.1743, '/', 'r')]
    public void Decodes_Compressed_Object(
        string infoText, string expectedName, bool expectedAlive,
        double expectedLat, double expectedLon,
        char expectedSymTable, char expectedSymCode)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsObjectDecoder.TryDecode(info, out var obj).Should().BeTrue();
        obj.Name.Should().Be(expectedName);
        obj.IsAlive.Should().Be(expectedAlive);
        obj.Position.Latitude.Should().BeApproximately(expectedLat, 1e-3);
        obj.Position.Longitude.Should().BeApproximately(expectedLon, 1e-3);
        obj.Position.SymbolTable.Should().Be(expectedSymTable);
        obj.Position.SymbolCode.Should().Be(expectedSymCode);
        obj.Position.Format.Should().Be(AprsPositionFormat.Compressed);
    }

    [Fact]
    public void Strips_DTI_If_Present()
    {
        var withDti    = System.Text.Encoding.ASCII.GetBytes(
            ";TESTOBJ  *111111z5301.01N/01745.68Er");
        var withoutDti = System.Text.Encoding.ASCII.GetBytes(
            "TESTOBJ  *111111z5301.01N/01745.68Er");
        AprsObjectDecoder.TryDecode(withDti,    out var a).Should().BeTrue();
        AprsObjectDecoder.TryDecode(withoutDti, out var b).Should().BeTrue();
        a.Should().Be(b);
    }
}
