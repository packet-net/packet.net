using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Properties;

/// <summary>
/// Fuzz properties for the AX.25 frame parser. These complement the
/// happy-path round-trip tests in <see cref="Ax25FrameRoundTripProperties"/>
/// by asserting that <see cref="Ax25Frame.TryParse"/> and
/// <see cref="Ax25Address.Read"/> degrade gracefully on arbitrary
/// (often nonsense) input - never throwing unexpected exception types,
/// never producing an output that doesn't round-trip.
/// </summary>
/// <remarks>
/// These are the post-deployment safety net: <c>TryParse</c> sits at a
/// trust boundary (it consumes whatever bytes a TNC delivers, which
/// includes garbage from RF noise and bug-emitting peers). Crashing on
/// a malformed frame is a denial-of-service against the whole link
/// layer; "return false" is the only acceptable failure mode.
/// </remarks>
public class Ax25ParserFuzzProperties
{
    /// <summary>
    /// For any byte array (including empty, oversize, all-zero, all-0xFF),
    /// <see cref="Ax25Frame.TryParse"/> must terminate without throwing
    /// any exception, and must produce either a non-null frame (when it
    /// returns true) or no frame (when false).
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void TryParse_Never_Throws(byte[] bytes)
    {
        bytes ??= [];
        // The Property attribute will fail the test if any exception
        // escapes the property body.
        bool ok = Ax25Frame.TryParse(bytes, out var frame);
        if (ok)
        {
            frame.Should().NotBeNull();
        }
        else
        {
            frame.Should().BeNull();
        }
    }

    /// <summary>
    /// Any frame that <c>TryParse</c> accepts must round-trip cleanly
    /// through <c>ToBytes</c> + <c>TryParse</c> - its byte form is
    /// canonical for the value it represents.
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void Parsed_Frame_Round_Trips_Through_ToBytes(byte[] bytes)
    {
        bytes ??= [];
        if (!Ax25Frame.TryParse(bytes, out var first))
        {
            return;
        }

        var rewritten = first!.ToBytes();
        Ax25Frame.TryParse(rewritten, out var second).Should().BeTrue();

        second!.Destination.Should().Be(first.Destination);
        second.Source.Should().Be(first.Source);
        second.Digipeaters.Should().Equal(first.Digipeaters);
        second.Control.Should().Be(first.Control);
        second.Pid.Should().Be(first.Pid);
        second.Info.ToArray().Should().Equal(first.Info.ToArray());
    }

    /// <summary>
    /// <see cref="Ax25Address.Read"/> on any 7-byte slot either returns
    /// a value or throws <see cref="ArgumentException"/>. No other
    /// exception types should escape (they'd indicate a parser bug
    /// rather than a malformed-input rejection).
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void Address_Read_Only_Throws_ArgumentException(byte[] bytes)
    {
        bytes ??= [];
        if (bytes.Length < Ax25Address.EncodedLength)
        {
            // Short span path; verify that's an ArgumentException too.
            try { Ax25Address.Read(bytes); }
            catch (ArgumentException) { return; }
            return;
        }

        try
        {
            var _ = Ax25Address.Read(bytes.AsSpan(0, Ax25Address.EncodedLength));
        }
        catch (ArgumentException)
        {
            // Expected on a malformed slot.
        }
    }

    /// <summary>
    /// Anything parsed-then-encoded should fit in the parser's claimed
    /// <c>RequiredBytes</c> budget exactly - no over-allocation, no
    /// silent truncation.
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void RequiredBytes_Is_Exact(byte[] bytes)
    {
        bytes ??= [];
        if (!Ax25Frame.TryParse(bytes, out var frame))
        {
            return;
        }

        var dest = new byte[frame!.RequiredBytes];
        int written = frame.WriteTo(dest);
        written.Should().Be(frame.RequiredBytes);
    }

    /// <summary>
    /// Round-trip for I-frames (the connected-mode workhorse). Builds
    /// arbitrary mod-8 control bytes and verifies the parser recovers
    /// the frame intact.
    /// </summary>
    [Property(MaxTest = 500)]
    public void I_Frame_Encode_Then_Decode_Roundtrips(
        byte ns, byte nr, bool pollFinal, byte pid, byte[] info)
    {
        info ??= [];
        // I-frame control byte: N(R) N(R) N(R) P N(S) N(S) N(S) 0
        byte control = (byte)(((nr & 0x07) << 5) | ((pollFinal ? 1 : 0) << 4) | ((ns & 0x07) << 1));
        var dest = new Callsign("WB2OSZ", 0);
        var src = new Callsign("M0LTE", 1);
        var destAddr = new Ax25Address(dest, CrhBit: true, ExtensionBit: false);
        var srcAddr = new Ax25Address(src, CrhBit: false, ExtensionBit: true);

        // Hand-assemble: dest + src + control + pid + info.
        var bytes = new byte[7 + 7 + 1 + 1 + info.Length];
        destAddr.Write(bytes.AsSpan(0));
        srcAddr.Write(bytes.AsSpan(7));
        bytes[14] = control;
        bytes[15] = pid;
        info.CopyTo(bytes.AsSpan(16));

        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        frame!.Control.Should().Be(control);
        frame.Pid.Should().Be(pid);
        frame.Info.ToArray().Should().Equal(info);
    }

    /// <summary>
    /// Empty-callsign source / destination (BPQ's `>IS` beacon, PD4R-12's
    /// `>,TEST` broadcast) round-trip. Regression for PR #85.
    /// </summary>
    [Property(MaxTest = 100)]
    public void Empty_Callsign_Address_Round_Trips(
        byte ssidRaw, bool isCommand, bool isExtension)
    {
        var addr = new Ax25Address(
            new Callsign("", (byte)(ssidRaw & 0x0F)),
            CrhBit: isCommand,
            ExtensionBit: isExtension);

        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        addr.Write(buf);
        var decoded = Ax25Address.Read(buf);
        decoded.Should().Be(addr);
        // All six callsign-character bytes must be the space-shift padding.
        for (int i = 0; i < 6; i++)
        {
            buf[i].Should().Be((byte)(' ' << 1));
        }
    }
}
