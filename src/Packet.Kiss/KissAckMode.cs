namespace Packet.Kiss;

/// <summary>
/// Helpers for the G8BPQ "ACKMODE" KISS extension (KISS command 0x0C).
/// </summary>
/// <remarks>
/// <para>
/// ACKMODE is documented in the multi-drop-kiss-operation spec:
/// the host sends <c>FEND | (port&lt;&lt;4)|0xC | seqHi | seqLo | payload | FEND</c>;
/// the TNC echoes back <c>FEND | (port&lt;&lt;4)|0xC | seqHi | seqLo | FEND</c>
/// when (and only when) the frame has actually been keyed onto the air.
/// </para>
/// <para>
/// The 2-byte tag is an opaque token chosen by the host. Hosts pair the
/// outbound tag with TX-completion timing so they can size T1 properly even
/// for slow modes where queue-acceptance ≠ transmit-completion.
/// </para>
/// <para>
/// This class is framing-neutral — it just sits on top of <see cref="KissCommand.AckMode"/>
/// and a <see cref="KissFrame"/>. The KISS encoder/decoder already handle SLIP framing,
/// command-byte port nibble, and FEND/FESC escapes uniformly.
/// </para>
/// </remarks>
public static class KissAckMode
{
    /// <summary>
    /// Build an ACKMODE outbound frame: command 0x0C followed by the 2-byte
    /// host-chosen sequence tag and the AX.25 payload bytes.
    /// </summary>
    public static byte[] BuildSendFrame(byte port, ushort sequenceTag, ReadOnlySpan<byte> ax25Payload)
    {
        var payload = new byte[ax25Payload.Length + 2];
        payload[0] = (byte)((sequenceTag >> 8) & 0xFF);
        payload[1] = (byte)(sequenceTag & 0xFF);
        ax25Payload.CopyTo(payload.AsSpan(2));
        return KissEncoder.Encode(port, KissCommand.AckMode, payload);
    }

    /// <summary>
    /// True if <paramref name="frame"/> is the TNC's TX-complete echo for an
    /// ACKMODE send: command 0x0C with a 2-byte payload (the sequence tag).
    /// </summary>
    /// <param name="frame">The decoded KISS frame.</param>
    /// <param name="sequenceTag">The 16-bit sequence tag recovered from the echo.</param>
    public static bool TryParseAcknowledgement(KissFrame frame, out ushort sequenceTag)
    {
        sequenceTag = 0;
        if (frame.Command != KissCommand.AckMode || frame.Payload.Length != 2)
        {
            return false;
        }
        sequenceTag = (ushort)((frame.Payload[0] << 8) | frame.Payload[1]);
        return true;
    }

    /// <summary>
    /// True if <paramref name="frame"/> is an ACKMODE *data* frame — command
    /// 0x0C with a payload of 2 sequence bytes followed by AX.25 bytes (length
    /// strictly greater than 2). Single-port TNCs do not normally emit
    /// inbound ACKMODE data, but multi-master / cross-link bridges can.
    /// </summary>
    public static bool TryParseDataFrame(KissFrame frame, out ushort sequenceTag, out ReadOnlyMemory<byte> ax25Payload)
    {
        sequenceTag = 0;
        ax25Payload = ReadOnlyMemory<byte>.Empty;
        if (frame.Command != KissCommand.AckMode || frame.Payload.Length <= 2)
        {
            return false;
        }
        sequenceTag = (ushort)((frame.Payload[0] << 8) | frame.Payload[1]);
        ax25Payload = frame.Payload.AsMemory(2);
        return true;
    }
}
