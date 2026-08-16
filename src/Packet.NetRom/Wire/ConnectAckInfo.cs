namespace Packet.NetRom.Wire;

/// <summary>
/// Codec for the information field of a NET/ROM L4 <b>Connect Acknowledge</b>
/// (opcode 0x02). The first octet, the <b>accepted send-window</b>, is base
/// NET/ROM and rides every acknowledgement; LinBPQ adds a second octet (a
/// time-to-live/flags byte) when the Connect Request came from a BPQ node, and
/// folds its compression-agreed bit into it.
/// </summary>
/// <remarks>
/// <para>Wire layout (LinBPQ <c>L4Code.c</c> <c>SendConACK</c>), 1 or 2 octets:</para>
/// <code>
///   [1] accepted send-window size          (always - base NET/ROM)
///   [1] TTL byte; bit 0x80 = "compression agreed" (L4DATA[1] |= 0x80)   (BPQ extension)
/// </code>
/// <para>
/// The window octet is <em>not</em> an extension: LinBPQ writes
/// <c>L3MSG-&gt;L4DATA[0] = L4-&gt;L4WINDOW</c> then sets
/// <c>LENGTH = MSGHDDRLEN + 22</c> (<c>L4Code.c:1768,1824</c>), a 21-byte vanilla
/// Connect Acknowledge, and reads it back unconditionally
/// (<c>L4-&gt;L4WINDOW = L3MSG-&gt;L4DATA[0]</c>, <c>L4Code.c:2287</c>); only the
/// <em>second</em> octet is length-gated on <c>BPQNODE</c>. Linux <c>af_netrom</c>
/// does the same: <c>nr_write_internal</c> emits <c>nr-&gt;window</c> with
/// <c>NR_CONNACK_LEN = 1</c> and the receive path reads <c>skb-&gt;data[20]</c>. A
/// peer that ignores the octet is unharmed; it is trailing info to it.
/// </para>
/// <para>
/// The compression bit is only ever set when <em>both</em> ends offered compression:
/// LinBPQ sets its circuit's <c>AllowCompress</c> on receiving the Connect Request's
/// compress bit (gated on its own <c>L4Compress</c> config) and only then mirrors it
/// back here. On receipt the originator masks the bit off before reading the TTL
/// (<c>L4DATA[1] &amp;= 0x7f</c>), so it is harmless to a peer that ignores it.
/// </para>
/// </remarks>
public static class ConnectAckInfo
{
    /// <summary>Octets in a vanilla (base NET/ROM) Connect Acknowledge info field: the
    /// accepted send-window alone.</summary>
    public const int VanillaLength = 1;

    /// <summary>Octets in the LinBPQ extended Connect Acknowledge info field.</summary>
    public const int ExtendedLength = 2;

    /// <summary>The largest window a peer may accept: the NET/ROM sequence space leaves
    /// bit 7 to the flags, so 127 is the ceiling, the same clamp the circuit applies to
    /// its own proposal.</summary>
    public const int MaxWindow = 127;

    /// <summary>The "compression agreed" bit, OR-ed into the TTL octet of an extended
    /// Connect Acknowledge (LinBPQ <c>L4Code.c</c>: <c>L3MSG->L4DATA[1] |= 0x80</c>).</summary>
    public const byte CompressBit = 0x80;

    /// <summary>
    /// Build the Connect Acknowledge info field: the accepted window (always), plus the
    /// TTL octet carrying the compression-agreed bit when
    /// <paramref name="agreeCompression"/> is true. The 1-octet form is the vanilla
    /// NET/ROM acknowledgement that LinBPQ and Linux both emit.
    /// </summary>
    public static byte[] Build(byte acceptedWindow, byte timeToLive, bool agreeCompression)
    {
        if (!agreeCompression)
        {
            return [acceptedWindow];
        }

        return [acceptedWindow, (byte)(timeToLive | CompressBit)];
    }

    /// <summary>
    /// Read the accepted send-window an acknowledging peer reported. Returns
    /// <c>false</c> for an absent octet (a terse peer that sent no info field at all) or
    /// an out-of-range value (0, or &gt; <see cref="MaxWindow"/>, a peer that put
    /// something else there); the originator then keeps the window it proposed. Mirrors
    /// LinBPQ's unconditional <c>L4WINDOW = L4DATA[0]</c> (<c>L4Code.c:2287</c>) and
    /// Linux's <c>skb->data[20]</c> read, with the sanity bound BPQ gets from its own
    /// <c>L4DEFAULTWINDOW</c> fallback (<c>L4Code.c:2010-2013</c>).
    /// </summary>
    public static bool TryReadAcceptedWindow(ReadOnlySpan<byte> info, out byte acceptedWindow)
    {
        acceptedWindow = 0;
        if (info.Length < VanillaLength || info[0] == 0 || info[0] > MaxWindow)
        {
            return false;
        }

        acceptedWindow = info[0];
        return true;
    }

    /// <summary>
    /// Read the BPQ compression-agreed bit from a Connect Acknowledge info field.
    /// Returns <c>false</c> for the short (vanilla, window-only) form. Mirrors LinBPQ's
    /// <c>L4DATA[1] &amp; 0x80</c> test on the second octet.
    /// </summary>
    public static bool AgreesCompression(ReadOnlySpan<byte> info)
        => info.Length >= ExtendedLength && (info[1] & CompressBit) != 0;
}
