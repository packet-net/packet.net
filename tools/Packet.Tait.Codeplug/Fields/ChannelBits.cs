namespace Packet.Tait.Codeplug;

/// <summary>
/// The channel table (record type 0x05) is one contiguous LSB-first bit-stream, a fixed number of
/// bits per channel, physically split across a run of ≤32-byte records (index 0, 1, 2, ...). A
/// channel's fields can therefore straddle a record boundary, so all the 0x05 record payloads must
/// be treated as a single concatenated bit array. This reader/writer maps a global bit offset onto
/// the right record byte in place, so writes land back in the underlying record payloads.
/// </summary>
public sealed class ChannelBits
{
    private readonly List<byte[]> _payloads;

    /// <summary>Build a stream over the 0x05 records in <paramref name="records"/> (index order).</summary>
    public ChannelBits(IEnumerable<CodeplugRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _payloads = records
            .Where(r => r.Section == 0x05)
            .OrderBy(r => r.Index)
            .Select(r => r.Data)
            .ToList();
    }

    /// <summary>Total number of bits across all channel records.</summary>
    public int TotalBits => _payloads.Sum(p => p.Length) * 8;

    /// <summary>Read <paramref name="bitLength"/> bits (LSB first) starting at global bit
    /// <paramref name="bitOffset"/>.</summary>
    public long GetBits(int bitOffset, int bitLength)
    {
        if (bitLength is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLength), bitLength, "1..63 bits");
        }

        long value = 0;
        for (int k = 0; k < bitLength; k++)
        {
            if (GetBit(bitOffset + k))
            {
                value |= 1L << k;
            }
        }

        return value;
    }

    /// <summary>Write the low <paramref name="bitLength"/> bits of <paramref name="value"/> (LSB
    /// first) at global bit <paramref name="bitOffset"/>.</summary>
    public void SetBits(int bitOffset, int bitLength, long value)
    {
        if (bitLength is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLength), bitLength, "1..63 bits");
        }

        if (bitLength < 63 && (value < 0 || value >= (1L << bitLength)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"does not fit {bitLength} bits");
        }

        for (int k = 0; k < bitLength; k++)
        {
            SetBit(bitOffset + k, ((value >> k) & 1) != 0);
        }
    }

    private bool GetBit(int globalBit)
    {
        (byte[] arr, int index) = Locate(globalBit);
        return ((arr[index] >> (globalBit & 7)) & 1) != 0;
    }

    private void SetBit(int globalBit, bool set)
    {
        (byte[] arr, int index) = Locate(globalBit);
        int mask = 1 << (globalBit & 7);
        if (set)
        {
            arr[index] = (byte)(arr[index] | mask);
        }
        else
        {
            arr[index] = (byte)(arr[index] & ~mask);
        }
    }

    private (byte[] Array, int Index) Locate(int globalBit)
    {
        int bytePos = globalBit >> 3;
        foreach (byte[] p in _payloads)
        {
            if (bytePos < p.Length)
            {
                return (p, bytePos);
            }

            bytePos -= p.Length;
        }

        throw new ArgumentOutOfRangeException(nameof(globalBit), globalBit, "past the end of the channel stream");
    }
}
