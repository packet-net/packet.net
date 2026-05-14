using Packet.Aprs;

namespace Packet.Aprs.Tests;

/// <summary>
/// Validates that the strict-vs-lenient option overloads on each APRS
/// decoder do what they say on the tin: <see cref="AprsParseOptions.Strict"/>
/// rejects inputs that <see cref="AprsParseOptions.Lenient"/> accepts.
/// </summary>
public class AprsDecoderOptionsTests
{
    // ─── Status (DTI >) ───────────────────────────────────────────────────

    [Fact]
    public void Status_Strict_Rejects_NonAscii_Body()
    {
        // Chinese-station beacon (UTF-8 bytes). Lenient must accept; Strict
        // must reject (§16 says printable ASCII only).
        var info = "BH3GZT>APFMO2:>正在 香港中文"u8.ToArray();
        // Strip everything up to the DTI ':' for the decoder.
        int colon = Array.IndexOf(info, (byte)':');
        var bodyAfterColon = info.AsSpan(colon + 1);

        AprsStatusDecoder.TryDecode(bodyAfterColon, AprsParseOptions.Lenient, out _).Should().BeTrue();
        AprsStatusDecoder.TryDecode(bodyAfterColon, AprsParseOptions.Strict, out _).Should().BeFalse();
    }

    [Fact]
    public void Status_Strict_Rejects_Pipe_Or_Tilde()
    {
        // §16: status body may NOT contain | or ~. Lenient tolerates;
        // Strict must reject.
        var withPipe   = System.Text.Encoding.ASCII.GetBytes(">Status with | pipe");
        var withTilde  = System.Text.Encoding.ASCII.GetBytes(">Status with ~ tilde");
        AprsStatusDecoder.TryDecode(withPipe,  AprsParseOptions.Lenient, out _).Should().BeTrue();
        AprsStatusDecoder.TryDecode(withPipe,  AprsParseOptions.Strict,  out _).Should().BeFalse();
        AprsStatusDecoder.TryDecode(withTilde, AprsParseOptions.Strict,  out _).Should().BeFalse();
    }

    [Fact]
    public void Status_Strict_Accepts_Plain_Ascii()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(">Net Control Center");
        AprsStatusDecoder.TryDecode(info, AprsParseOptions.Strict, out var s).Should().BeTrue();
        s.Text.Should().Be("Net Control Center");
    }

    // ─── Telemetry (DTI T) ───────────────────────────────────────────────

    [Theory]
    [InlineData("T#026,0,0,0,42,1,00000000")]            // variable-width
    [InlineData("T#949,3.2,0.0,16.0,0.0,0.0,00000000")]  // floats
    public void Telemetry_Strict_Rejects_NonInteger_Values(string raw)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(raw);
        AprsTelemetryDecoder.TryDecode(info, AprsParseOptions.Lenient, out _).Should().BeTrue();
        AprsTelemetryDecoder.TryDecode(info, AprsParseOptions.Strict,  out _).Should().BeFalse();
    }

    [Fact]
    public void Telemetry_Strict_Accepts_Spec_Conformant_Frame()
    {
        // APRS101 §13 example: 3-digit zero-padded, all in 000–255.
        var info = System.Text.Encoding.ASCII.GetBytes("T#005,199,000,255,073,123,01101001");
        AprsTelemetryDecoder.TryDecode(info, AprsParseOptions.Strict, out var t).Should().BeTrue();
        t.AnalogValues.Should().Equal(199.0, 0.0, 255.0, 73.0, 123.0);
    }

    [Fact]
    public void Telemetry_Strict_Rejects_Value_Out_Of_Range()
    {
        // 3-digit but > 255.
        var info = System.Text.Encoding.ASCII.GetBytes("T#005,300,000,255,073,123,01101001");
        AprsTelemetryDecoder.TryDecode(info, AprsParseOptions.Strict, out _).Should().BeFalse();
    }

    // ─── Mic-E (DTI ` / ' / 0x1C / 0x1D) ─────────────────────────────────

    [Fact]
    public void MicE_Strict_Rejects_Legacy_Dti_Bytes()
    {
        // Construct a minimal valid info field with DTI = 0x1C (Rev. 0 beta).
        var info = new byte[]
        {
            0x1C, (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O',
            (byte)'>', (byte)'/'
        };
        AprsMicEDecoder.TryDecode("S32U6T", info, AprsParseOptions.Lenient, out _).Should().BeTrue();
        AprsMicEDecoder.TryDecode("S32U6T", info, AprsParseOptions.Strict,  out _).Should().BeFalse();
    }

    [Fact]
    public void MicE_Strict_Accepts_Canonical_Dti()
    {
        // Same payload but with backtick DTI — canonical per §10.
        var info = new byte[]
        {
            (byte)'`', (byte)'(', (byte)'(', (byte)'N', (byte)'$', (byte)'Z', (byte)'O',
            (byte)'>', (byte)'/'
        };
        AprsMicEDecoder.TryDecode("S32U6T", info, AprsParseOptions.Strict, out var r).Should().BeTrue();
        r.Latitude.Should().BeApproximately(33.4273, 1e-3);
    }
}
