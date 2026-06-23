namespace Packet.NetRom.Wire;

/// <summary>
/// Codec for the information field of a NET/ROM L4 <b>Connect Acknowledge</b>
/// (opcode 0x02) in the LinBPQ <em>extended</em> form. Vanilla NET/ROM sends a
/// Connect Acknowledge with an empty info field; LinBPQ, when the Connect Request
/// came from a BPQ node, replies with two octets — the accepted send-window and a
/// time-to-live/flags octet — and folds its compression-agreed bit into the latter.
/// </summary>
/// <remarks>
/// <para>Wire layout (LinBPQ <c>L4Code.c</c> Connect Acknowledge build), 2 octets:</para>
/// <code>
///   [1] accepted send-window size
///   [1] TTL byte; bit 0x80 = "compression agreed" (L4DATA[1] |= 0x80)
/// </code>
/// <para>
/// The bit is only ever set when <em>both</em> ends offered compression — LinBPQ sets
/// its circuit's <c>AllowCompress</c> on receiving the Connect Request's compress bit
/// (gated on its own <c>L4Compress</c> config) and only then mirrors it back here. On
/// receipt the originator masks the bit off before reading the TTL
/// (<c>L4DATA[1] &amp;= 0x7f</c>), so it is harmless to a peer that ignores it.
/// </para>
/// </remarks>
public static class ConnectAckInfo
{
    /// <summary>Octets in the LinBPQ extended Connect Acknowledge info field.</summary>
    public const int ExtendedLength = 2;

    /// <summary>The "compression agreed" bit, OR-ed into the TTL octet of an extended
    /// Connect Acknowledge (LinBPQ <c>L4Code.c</c>: <c>L3MSG->L4DATA[1] |= 0x80</c>).</summary>
    public const byte CompressBit = 0x80;

    /// <summary>
    /// Build the extended Connect Acknowledge info field: accepted window + TTL octet,
    /// with the compression-agreed bit set when <paramref name="agreeCompression"/> is
    /// true. Returns an empty array when no extension is needed (vanilla form), so a
    /// circuit that did not negotiate compression stays byte-for-byte the plain
    /// NET/ROM Connect Acknowledge.
    /// </summary>
    public static byte[] Build(byte acceptedWindow, byte timeToLive, bool agreeCompression)
    {
        if (!agreeCompression)
        {
            return [];
        }

        return [acceptedWindow, (byte)(timeToLive | CompressBit)];
    }

    /// <summary>
    /// Read the BPQ compression-agreed bit from a Connect Acknowledge info field.
    /// Returns <c>false</c> for the empty / short (vanilla) form. Mirrors LinBPQ's
    /// <c>L4DATA[1] &amp; 0x80</c> test on the second octet.
    /// </summary>
    public static bool AgreesCompression(ReadOnlySpan<byte> info)
        => info.Length >= ExtendedLength && (info[1] & CompressBit) != 0;
}
