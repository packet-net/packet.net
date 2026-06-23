using System.IO.Compression;

namespace Packet.NetRom.Transport;

/// <summary>
/// The payload (de)compressor for a compression-negotiated NET/ROM L4 circuit,
/// matching LinBPQ's on-wire scheme: a raw <b>zlib stream (RFC 1950)</b> — a 2-octet
/// zlib header, a DEFLATE body, and an Adler-32 trailer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why zlib, and why this interoperates with LinBPQ.</b> LinBPQ decompresses a
/// received compressed circuit payload with <c>doinflate</c> (<c>DRATS.c</c>), which is
/// a plain <c>inflateInit</c> / <c>inflate</c> over the received bytes — i.e. it expects
/// a standard zlib-wrapped stream (default window bits, zlib header + Adler-32), the
/// same format LinBPQ's own <c>Compressit</c> (<c>HTTPcode.c</c>) emits via
/// <c>deflateInit(&amp;strm, Z_BEST_COMPRESSION)</c>. .NET's <see cref="ZLibStream"/> reads
/// and writes exactly that RFC 1950 framing, so a stream we deflate is accepted by
/// LinBPQ's <c>doinflate</c>, and a stream LinBPQ deflates is accepted by our
/// <see cref="TryDecompress"/>. The compression is thus interoperable at the <em>format</em>
/// level (the only level that matters for correctness); the exact compressed bytes are
/// not required to match — zlib decompression is independent of the encoder's choices.
/// </para>
/// <para>
/// <b>What is <em>not</em> reproduced.</b> LinBPQ's send side opportunistically batches
/// several queued circuit frames into one compression unit and re-chunks on overflow;
/// that is a throughput heuristic, not part of the wire contract. We compress each
/// logical send as one unit and fragment with the more-follows flag — a valid,
/// decodable stream either way. (LinBPQ also references a never-defined
/// <c>L2Compressit</c> for the compress direction in the M0LTE source tree, so there is
/// no byte-exact encoder to match; <c>doinflate</c> is the authoritative, stable contract.)
/// </para>
/// </remarks>
public static class NetRomCompression
{
    /// <summary>
    /// Compress <paramref name="data"/> into a zlib stream (RFC 1950) that LinBPQ's
    /// <c>doinflate</c> accepts. Uses the smallest-output level
    /// (<see cref="CompressionLevel.SmallestSize"/>) — air time is the scarce resource.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decompress a zlib stream produced by LinBPQ (or by <see cref="Compress"/>) back to
    /// the original bytes. Returns <c>false</c> (never throws) if <paramref name="data"/>
    /// is not a valid zlib stream or expands past <paramref name="maxOutput"/> — a
    /// corrupt or truncated compressed frame must fail closed, not crash the circuit.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> data, int maxOutput, out byte[] result)
    {
        result = [];
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            var buffer = new byte[4096];
            int total = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxOutput)
                {
                    return false;
                }
                output.Write(buffer, 0, read);
            }

            result = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
