using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Tests;

public class Ax25FrameOptionsTests
{
    [Fact]
    public void Strict_Rejects_Frame_With_Empty_Source_Callsign()
    {
        // BPQ ID beacon shape: dest "IS", source all-space.
        Span<byte> bytes = stackalloc byte[16];
        // Dest "IS" + 4 padding
        bytes[0] = (byte)('I' << 1);
        bytes[1] = (byte)('S' << 1);
        for (int i = 2; i < 6; i++) bytes[i] = (byte)(' ' << 1);
        bytes[6] = 0xE0;   // C=1, R=11, SSID=0, E=0
        // Source: all space
        for (int i = 7; i < 13; i++) bytes[i] = (byte)(' ' << 1);
        bytes[13] = 0x61;  // C=0, R=11, SSID=0, E=1
        bytes[14] = 0x03;  // UI control
        bytes[15] = 0xF0;  // PID (No Layer 3)

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out _).Should().BeFalse();
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, out var frame).Should().BeTrue();
        frame!.Source.Callsign.Base.Should().Be("");
    }

    [Fact]
    public void Strict_Rejects_Trailing_Bytes_On_Supervisory_Frame()
    {
        // Two normal addresses + RR control (0x01) + garbage trailing bytes.
        // RR is a supervisory frame; §3.5 says it carries no info field.
        Span<byte> bytes = stackalloc byte[20];
        WriteCallsign("M0LTE",   1, 0xE0, bytes[..7]);  // dest, C=1
        WriteCallsign("WB2OSZ",  0, 0x61, bytes[7..14]); // source, C=0, E=1
        bytes[14] = 0x01;                                // RR
        bytes[15] = 0xDE; bytes[16] = 0xAD;
        bytes[17] = 0xBE; bytes[18] = 0xEF;
        bytes[19] = 0x42;

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out _).Should().BeFalse();
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, out var frame).Should().BeTrue();
        frame!.Info.Length.Should().Be(5);
    }

    [Fact]
    public void Strict_Accepts_Trailing_Bytes_On_FRMR()
    {
        // §3.5 explicitly permits info on FRMR.
        Span<byte> bytes = stackalloc byte[20];
        WriteCallsign("M0LTE",  1, 0xE0, bytes[..7]);
        WriteCallsign("WB2OSZ", 0, 0x61, bytes[7..14]);
        bytes[14] = 0x87;   // FRMR
        bytes[15] = 0x12; bytes[16] = 0x34;
        bytes[17] = 0x56; bytes[18] = 0x78;
        bytes[19] = 0x9A;

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out var frame).Should().BeTrue();
        frame!.Info.Length.Should().Be(5);
    }

    [Fact]
    public void Strict_Rejects_Sabm_With_Response_Cbits_Lenient_Accepts()
    {
        // §4.3.3.1 / §6.1.2: SABM is ALWAYS a command. A SABM whose address C-bits mark
        // it a RESPONSE (dest C=0, source C=1) is malformed. Strict drops it at decode (so
        // a bogus-direction SABM can never open a session); the lenient default accepts it
        // — a legacy AX.25 v1.x peer predates the v2.0 command/response C-bit encoding.
        Span<byte> bytes = stackalloc byte[15];
        WriteCallsign("M0LTE",  0, 0x60, bytes[..7]);    // dest, C=0 (response direction)
        WriteCallsign("WB2OSZ", 0, 0xE1, bytes[7..14]);  // source, C=1, E=1
        bytes[14] = 0x2F;                                 // SABM

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out _).Should().BeFalse();
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, out var frame).Should().BeTrue();
        frame!.IsCommand.Should().BeFalse();
    }

    [Fact]
    public void Strict_Accepts_A_Well_Formed_Command_Sabm()
    {
        // The well-formed case: dest C=1, source C=0 → IsCommand. Strict accepts.
        Span<byte> bytes = stackalloc byte[15];
        WriteCallsign("M0LTE",  0, 0xE0, bytes[..7]);    // dest, C=1 (command)
        WriteCallsign("WB2OSZ", 0, 0x61, bytes[7..14]);  // source, C=0, E=1
        bytes[14] = 0x2F;                                 // SABM

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out var frame).Should().BeTrue();
        frame!.IsCommand.Should().BeTrue();
    }

    [Fact]
    public void Parameterless_TryParse_Uses_Lenient()
    {
        // Same input as the Strict-rejects-RR-trailing-bytes test.
        // The back-compat overload must accept it.
        Span<byte> bytes = stackalloc byte[20];
        WriteCallsign("M0LTE",  1, 0xE0, bytes[..7]);
        WriteCallsign("WB2OSZ", 0, 0x61, bytes[7..14]);
        bytes[14] = 0x01;
        bytes[15] = 0xDE; bytes[16] = 0xAD; bytes[17] = 0xBE;
        bytes[18] = 0xEF; bytes[19] = 0x42;

        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        frame!.Info.Length.Should().Be(5);
    }

    private static void WriteCallsign(string callsign, byte ssid, byte ssidByte, Span<byte> dest)
    {
        for (int i = 0; i < 6; i++)
        {
            char c = i < callsign.Length ? callsign[i] : ' ';
            dest[i] = (byte)(c << 1);
        }
        // Caller-supplied SSID byte already has the C / E / SSID bits set.
        // Ignore the `ssid` parameter — kept for readability at the call site.
        dest[6] = ssidByte;
        _ = ssid;
    }
}
