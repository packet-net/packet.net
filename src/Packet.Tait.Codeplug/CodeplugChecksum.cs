namespace Packet.Tait.Codeplug;

/// <summary>
/// The Tait codeplug record checksum. Same family as CCDI (modulo-256 sum, two's-complemented),
/// but computed over the DECODED record bytes (address hi, address lo, length, then the data
/// bytes) rather than over ASCII characters. The property that falls out: the sum of every
/// decoded byte in a complete record, checksum byte included, is 0 modulo 256.
///
/// Confirmed against the captured wire and the saved .m8p: e.g. the record 2700025E0079 decodes
/// to bytes 27 00 02 5E 00 with checksum 79, and 27+00+02+5E+00 = 87, (-87) and 0xFF = 79.
/// </summary>
public static class CodeplugChecksum
{
    /// <summary>Compute the checksum byte for the header-and-data portion of a record
    /// (address hi, address lo, length, data...).</summary>
    public static byte Compute(ReadOnlySpan<byte> headerAndData)
    {
        int sum = 0;
        foreach (byte b in headerAndData)
        {
            sum += b;
        }

        return (byte)(-sum);
    }

    /// <summary>True when the whole decoded record (header, data, and the trailing checksum byte)
    /// sums to 0 modulo 256.</summary>
    public static bool IsWholeRecordValid(ReadOnlySpan<byte> decodedRecordWithChecksum)
    {
        int sum = 0;
        foreach (byte b in decodedRecordWithChecksum)
        {
            sum += b;
        }

        return (sum & 0xFF) == 0;
    }
}
