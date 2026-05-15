using System.Buffers.Binary;
using System.Text;

namespace Packet.Agw;

/// <summary>
/// One AGW frame on the wire. The protocol's framing is a fixed
/// 36-byte header followed by <see cref="Data"/>'s bytes.
/// </summary>
/// <param name="Port">Radio port number (0..3 by AGW convention; some servers extend to 0..15).</param>
/// <param name="Kind">Command-kind ASCII letter (see <see cref="AgwCommandKind"/>).</param>
/// <param name="Pid">AX.25 PID. 0xF0 ("no layer 3") is the canonical default for text data.</param>
/// <param name="From">Source callsign (10-char ASCII, NUL-padded on the wire). Empty when not applicable.</param>
/// <param name="To">Destination callsign (10-char ASCII, NUL-padded on the wire). Empty when not applicable.</param>
/// <param name="UserField">4 bytes of user-defined data in the header. Some server replies (e.g. version, port-info) carry uint32 values here. Zero by default.</param>
/// <param name="Data">Frame body. Length is encoded in the header's data-length field.</param>
/// <remarks>
/// Callsigns are encoded as fixed-width 10-byte ASCII fields padded
/// with NUL bytes. SSIDs are included in the string (e.g. <c>"M0LTE-1"</c>),
/// not split out into a separate byte — that's part of AGW's
/// callsign-as-string ergonomic compared to KISS's address-byte
/// encoding.
/// </remarks>
public sealed record AgwFrame(
    byte Port,
    byte Kind,
    byte Pid,
    string From,
    string To,
    ReadOnlyMemory<byte> Data,
    uint UserField = 0)
{
    /// <summary>Fixed AGW header size in bytes.</summary>
    public const int HeaderSize = 36;

    /// <summary>Maximum bytes a callsign field may contain on the wire (10 ASCII + implicit NUL pad).</summary>
    public const int CallsignFieldSize = 10;

    /// <summary>
    /// Serialise this frame to a fresh byte array. The first
    /// <see cref="HeaderSize"/> bytes are the header, followed by
    /// <see cref="Data"/>'s bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        var buf = new byte[HeaderSize + Data.Length];
        WriteHeader(buf);
        Data.Span.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>
    /// Serialise the header into the first <see cref="HeaderSize"/>
    /// bytes of <paramref name="destination"/>. Caller is responsible
    /// for the body bytes.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="destination"/> is shorter than the header.</exception>
    public void WriteHeader(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"AGW header needs {HeaderSize} bytes, got {destination.Length}.", nameof(destination));

        destination.Clear();
        destination[0] = Port;
        destination[4] = Kind;
        destination[6] = Pid;
        WriteCallsign(destination.Slice(8, CallsignFieldSize), From);
        WriteCallsign(destination.Slice(18, CallsignFieldSize), To);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), (uint)Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(32, 4), UserField);
    }

    /// <summary>
    /// Parse a frame's worth of bytes off the head of
    /// <paramref name="buffer"/>. The buffer must contain at least
    /// the header AND the body the header advertises. Use
    /// <see cref="TryReadDataLength"/> first if you're reading from a
    /// stream and don't yet know how much data to wait for.
    /// </summary>
    /// <param name="buffer">Bytes starting at the beginning of a frame.</param>
    /// <param name="bytesConsumed">Total bytes consumed (header + body).</param>
    /// <returns>The parsed frame.</returns>
    /// <exception cref="InvalidDataException">Header malformed or buffer too short.</exception>
    public static AgwFrame Parse(ReadOnlySpan<byte> buffer, out int bytesConsumed)
    {
        if (buffer.Length < HeaderSize)
            throw new InvalidDataException($"AGW frame needs at least a {HeaderSize}-byte header; got {buffer.Length}.");

        byte port = buffer[0];
        byte kind = buffer[4];
        byte pid  = buffer[6];
        string from = ReadCallsign(buffer.Slice(8, CallsignFieldSize));
        string to   = ReadCallsign(buffer.Slice(18, CallsignFieldSize));
        uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, 4));
        uint user    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32, 4));

        if (dataLen > int.MaxValue - HeaderSize)
            throw new InvalidDataException($"AGW frame advertises data length {dataLen} which would overflow Int32.");

        int total = HeaderSize + (int)dataLen;
        if (buffer.Length < total)
            throw new InvalidDataException($"AGW frame body short: header advertises {dataLen} bytes, only {buffer.Length - HeaderSize} available.");

        var data = buffer.Slice(HeaderSize, (int)dataLen).ToArray();
        bytesConsumed = total;
        return new AgwFrame(port, kind, pid, from, to, data, user);
    }

    /// <summary>
    /// Read the body-length field from a header without parsing the
    /// rest of the frame. Returns false if <paramref name="header"/>
    /// is too short. Lets a streaming reader decide how many more
    /// bytes to await before invoking <see cref="Parse"/>.
    /// </summary>
    public static bool TryReadDataLength(ReadOnlySpan<byte> header, out int dataLength)
    {
        if (header.Length < HeaderSize)
        {
            dataLength = 0;
            return false;
        }
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(28, 4));
        if (raw > int.MaxValue - HeaderSize)
            throw new InvalidDataException($"AGW frame advertises data length {raw} which would overflow Int32.");
        dataLength = (int)raw;
        return true;
    }

    private static void WriteCallsign(Span<byte> destination, string callsign)
    {
        // Callsign field is fixed 10 bytes ASCII, NUL-padded. The wire
        // tolerates longer strings via truncation; we error so callers
        // catch typos early.
        if (callsign.Length > CallsignFieldSize)
            throw new ArgumentException($"AGW callsign field is {CallsignFieldSize} bytes; got '{callsign}' ({callsign.Length} chars).", nameof(callsign));

        destination.Clear();
        Encoding.ASCII.GetBytes(callsign, destination);
    }

    private static string ReadCallsign(ReadOnlySpan<byte> field)
    {
        // Trim at the first NUL — that's the field's terminator
        // convention. Some servers pad with spaces instead; trim those
        // too to be tolerant.
        int end = field.IndexOf((byte)0);
        if (end < 0) end = field.Length;
        return Encoding.ASCII.GetString(field.Slice(0, end)).TrimEnd();
    }
}
