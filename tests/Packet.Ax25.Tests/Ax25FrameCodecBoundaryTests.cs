using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Tests;

/// <summary>
/// Boundary and malformed-input tests for the AX.25 wire codec, driven by a
/// mutation-testing audit (Stryker). Each test pins a behaviour the existing
/// suite left unverified: truncated headers, over-long digipeater chains,
/// ambiguous command/response C-bit combinations, and short output buffers.
/// All cases assert the current spec-compliant behaviour; none widen the parser.
/// </summary>
public class Ax25FrameCodecBoundaryTests
{
    private static readonly Callsign Dest = new("M0LTE", 0);
    private static readonly Callsign Src = new("G7XYZ", 7);

    // ─── Command / response C-bit classification ────────────────────────

    [Theory]
    [InlineData(true, true)]    // both slots marked command, ambiguous
    [InlineData(false, false)]  // both slots marked response, ambiguous
    public void Ambiguous_C_Bits_Are_Neither_Command_Nor_Response(bool destCrh, bool srcCrh)
    {
        // §6.1.2 defines command as dest C=1/source C=0 and response as the
        // reverse. When both slots carry the same C-bit the direction is
        // ambiguous, and the frame must be classified as neither.
        var bytes = FrameBytes(
            new[]
            {
                new Ax25Address(Dest, CrhBit: destCrh, ExtensionBit: false),
                new Ax25Address(Src, CrhBit: srcCrh, ExtensionBit: true),
            },
            Ax25Frame.ControlUi, Ax25Frame.PidNoLayer3);

        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        frame!.IsCommand.Should().BeFalse("command requires dest C=1 AND source C=0");
        frame.IsResponse.Should().BeFalse("response requires dest C=0 AND source C=1");
    }

    // ─── Malformed address chains ───────────────────────────────────────

    [Fact]
    public void TryParse_Rejects_Destination_With_Extension_Bit_Set()
    {
        // E-bit set on the destination means the address field ends before a
        // source address is present, which is malformed (§3.12.5).
        var bytes = FrameBytes(
            new[] { new Ax25Address(Dest, CrhBit: true, ExtensionBit: true) },
            new byte[8]); // padding so the frame clears the minimum-length check

        Ax25Frame.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Nine_Digipeaters()
    {
        // §3.12.5 caps the repeater field at 8 digipeaters. Nine slots with the
        // E-bit only on the ninth must be rejected, not accepted as a 9-repeater path.
        var addresses = new List<Ax25Address>
        {
            new(Dest, CrhBit: true, ExtensionBit: false),
            new(Src, CrhBit: false, ExtensionBit: false),
        };
        for (int i = 0; i < 9; i++)
        {
            bool last = i == 8;
            addresses.Add(new Ax25Address(new Callsign($"DIGI{i}", 0), CrhBit: false, ExtensionBit: last));
        }

        var bytes = FrameBytes(addresses.ToArray(), Ax25Frame.ControlUi, Ax25Frame.PidNoLayer3);

        Ax25Frame.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Truncated_Digipeater_Slot()
    {
        // Source E-bit clear promises a digipeater follows, but the bytes end
        // before a full 7-octet slot is present.
        var bytes = FrameBytes(
            new[]
            {
                new Ax25Address(Dest, CrhBit: true, ExtensionBit: false),
                new Ax25Address(Src, CrhBit: false, ExtensionBit: false),
            },
            0x40); // a single stray byte, not enough for a digipeater slot

        Ax25Frame.TryParse(bytes, out _).Should().BeFalse();
    }

    // ─── Truncated headers (control / control-extension / PID missing) ──

    [Fact]
    public void TryParse_Rejects_Address_Field_With_No_Control_Byte()
    {
        // Complete address field, then nothing, so no control octet.
        var bytes = FrameBytes(
            new[]
            {
                new Ax25Address(Dest, CrhBit: true, ExtensionBit: false),
                new Ax25Address(Src, CrhBit: false, ExtensionBit: false),
                new Ax25Address(new Callsign("DIGI1", 0), CrhBit: false, ExtensionBit: true),
            });

        Ax25Frame.TryParse(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Extended_IFrame_Missing_Second_Control_Octet()
    {
        // An I frame (control bit 0 = 0) on a modulo-128 link needs two control
        // octets; here only the first is present.
        var bytes = FrameBytes(
            new[]
            {
                new Ax25Address(Dest, CrhBit: true, ExtensionBit: false),
                new Ax25Address(Src, CrhBit: false, ExtensionBit: true),
            },
            0x00); // I-frame first control octet only, no second octet

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_UIFrame_Missing_Pid()
    {
        // A UI frame must carry a PID octet after the control; here it is absent.
        var bytes = FrameBytes(
            new[]
            {
                new Ax25Address(Dest, CrhBit: true, ExtensionBit: false),
                new Ax25Address(Src, CrhBit: false, ExtensionBit: true),
            },
            Ax25Frame.ControlUi); // control only, no PID

        Ax25Frame.TryParse(bytes, out _).Should().BeFalse();
    }

    // ─── Output-buffer sizing ───────────────────────────────────────────

    [Fact]
    public void WriteTo_Throws_When_Destination_Too_Short()
    {
        var frame = Ax25Frame.Ui(Dest, Src, info: new byte[] { 0x01, 0x02 });
        var shortBuffer = new byte[frame.RequiredBytes - 1];

        var act = () => frame.WriteTo(shortBuffer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WriteToWithFcs_Throws_When_Destination_Has_No_Room_For_Fcs()
    {
        var frame = Ax25Frame.Ui(Dest, Src, info: new byte[] { 0x01, 0x02 });
        // Exactly enough for the body, two bytes short of the FCS trailer.
        var shortBuffer = new byte[frame.RequiredBytes];

        var act = () => frame.WriteToWithFcs(shortBuffer);

        act.Should().Throw<ArgumentException>();
    }

    // ─── Helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serialise a sequence of address slots followed by trailing octets
    /// (control / PID / info) into one KISS-form byte array.
    /// </summary>
    private static byte[] FrameBytes(Ax25Address[] addresses, params byte[] trailing)
    {
        var result = new List<byte>(addresses.Length * Ax25Address.EncodedLength + trailing.Length);
        var slot = new byte[Ax25Address.EncodedLength];
        foreach (var address in addresses)
        {
            Array.Clear(slot);
            address.Write(slot);
            result.AddRange(slot);
        }

        result.AddRange(trailing);
        return result.ToArray();
    }
}
