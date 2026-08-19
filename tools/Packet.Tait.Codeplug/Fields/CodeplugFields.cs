namespace Packet.Tait.Codeplug;

/// <summary>
/// A typed, version-pinned view over a <see cref="CodeplugImage"/>: the codeplug fields whose
/// on-the-wire encoding is known and pinned by tests. Construct via <see cref="Open"/>, which
/// refuses a database version this map has not been validated against (offsets are only valid for
/// a given DB version). Field values round-trip byte-for-byte; still bench-verify before trusting a
/// change on a radio.
///
/// Layout facts:
/// - Channels (record type 0x05) are one contiguous LSB-first bit-stream, 181 bits per channel,
///   split across ≤32-byte records; channel N's fields live at bit N*181 + the field offset.
/// - The data/signalling block is record 0x09/0; the audio block is record 0x3B/0. Their fields are
///   bit-packed at fixed byte/bit positions in the record payload.
/// </summary>
public sealed class CodeplugFields
{
    /// <summary>Database versions this field map is validated for.</summary>
    public static readonly IReadOnlySet<string> SupportedDbVersions =
        new HashSet<string>(StringComparer.Ordinal) { "0094", "0095" };

    private const int ChannelStrideBits = 181;

    // Channel field bit offsets, relative to the start of a channel.
    private const int ChSeparateTx = 0;    // 1 bit
    private const int ChTxFreq = 16;       // 32 bits, Hz
    private const int ChRxFreq = 48;       // 32 bits, Hz
    private const int ChBandwidth = 80;    // 2 bits
    private const int ChTxPower = 109;     // 3 bits

    private readonly ChannelBits _channels;

    private CodeplugFields(CodeplugImage image)
    {
        Image = image;
        _channels = new ChannelBits(image.Records);
    }

    /// <summary>The underlying image (mutations to fields are visible here).</summary>
    public CodeplugImage Image { get; }

    /// <summary>Open a typed view, or throw if the codeplug's database version is not mapped.</summary>
    public static CodeplugFields Open(CodeplugImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string ver = image.DatabaseVersionFromRecord ?? image.DatabaseVersion ?? "(none)";
        if (!SupportedDbVersions.Contains(ver))
        {
            throw new NotSupportedException(
                $"codeplug database version '{ver}' is not mapped (supported: " +
                $"{string.Join(", ", SupportedDbVersions)}); refusing to interpret fields.");
        }

        return new CodeplugFields(image);
    }

    /// <summary>True when this image's database version is one the field map covers.</summary>
    public static bool IsSupported(CodeplugImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string? ver = image.DatabaseVersionFromRecord ?? image.DatabaseVersion;
        return ver is not null && SupportedDbVersions.Contains(ver);
    }

    // ---- Channels -----------------------------------------------------------------------

    /// <summary>Number of channels, derived from the channel bit-stream length.</summary>
    public int ChannelCount => _channels.TotalBits / ChannelStrideBits;

    /// <summary>TX frequency in Hz for a channel.</summary>
    public long GetTxFrequencyHz(int channel) => Ch(channel, ChTxFreq, 32);

    /// <summary>RX frequency in Hz for a channel.</summary>
    public long GetRxFrequencyHz(int channel) => Ch(channel, ChRxFreq, 32);

    /// <summary>Set a channel's TX frequency in Hz.</summary>
    public void SetTxFrequencyHz(int channel, long hz) => SetCh(channel, ChTxFreq, 32, RequireFreq(hz));

    /// <summary>Set a channel's RX frequency in Hz.</summary>
    public void SetRxFrequencyHz(int channel, long hz) => SetCh(channel, ChRxFreq, 32, RequireFreq(hz));

    /// <summary>Whether the channel transmits on a different frequency than it receives.</summary>
    public bool GetSeparateTxFrequency(int channel) => Ch(channel, ChSeparateTx, 1) != 0;

    /// <summary>Set whether the channel transmits on a different frequency than it receives.</summary>
    public void SetSeparateTxFrequency(int channel, bool value) => SetCh(channel, ChSeparateTx, 1, value ? 1 : 0);

    /// <summary>Channel bandwidth.</summary>
    public Bandwidth GetBandwidth(int channel) => (Bandwidth)Ch(channel, ChBandwidth, 2);

    /// <summary>Set channel bandwidth.</summary>
    public void SetBandwidth(int channel, Bandwidth value) => SetCh(channel, ChBandwidth, 2, (long)value);

    /// <summary>Channel transmit power level.</summary>
    public PowerLevel GetPowerLevel(int channel) => (PowerLevel)Ch(channel, ChTxPower, 3);

    /// <summary>Set channel transmit power level.</summary>
    public void SetPowerLevel(int channel, PowerLevel value) => SetCh(channel, ChTxPower, 3, (long)value);

    private long Ch(int channel, int offset, int length)
    {
        RequireChannel(channel);
        return _channels.GetBits((channel * ChannelStrideBits) + offset, length);
    }

    private void SetCh(int channel, int offset, int length, long value)
    {
        RequireChannel(channel);
        _channels.SetBits((channel * ChannelStrideBits) + offset, length, value);
    }

    private void RequireChannel(int channel)
    {
        if (channel < 0 || channel >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"0..{ChannelCount - 1}");
        }
    }

    private static long RequireFreq(long hz)
    {
        if (hz < 0 || hz > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "0..4294967295 Hz");
        }

        return hz;
    }

    // ---- Data / signalling block (record 0x09/0) ----------------------------------------

    private byte[] Data => Image.Require(0x09, 0).Data;

    /// <summary>SDM (short data message) reception/transmission enabled.</summary>
    public bool SdmEnabled
    {
        get => (Data[10] & 0x40) != 0;
        set
        {
            byte[] p = Data;
            if (value) { p[10] |= 0x40; p[19] |= 0x38; }
            else { p[10] &= 0xBF; p[19] &= 0xC7; }
        }
    }

    /// <summary>THSD (high-speed data) modem master enable. The transparent-mode baud field is only
    /// meaningful when this is on.</summary>
    public bool ThsdModemEnabled
    {
        get => (Data[15] & 0x08) != 0;
        set { if (value) { Data[15] |= 0x08; } else { Data[15] &= 0xF7; } }
    }

    /// <summary>THSD transparent-mode (FFSK data operation) enabled.</summary>
    public bool TransparentModeEnabled
    {
        get => (Data[0] & 0x01) != 0;
        set
        {
            byte[] p = Data;
            if (value) { p[0] |= 0x01; p[19] |= 0x40; p[20] = 0x01; }
            else { p[0] &= 0xFE; p[19] &= 0xBF; p[20] = 0x00; }
        }
    }

    /// <summary>Data-port routing (low two bits of payload byte 14).</summary>
    public DataPort DataPort
    {
        get => (DataPort)(Data[14] & 0x03);
        set => Data[14] = (byte)((Data[14] & 0xFC) | ((byte)value & 0x03));
    }

    /// <summary>FFSK transparent-mode baud, a 3-bit index split across payload[12] bits[7:6] (low
    /// two bits of the index) and payload[13] bit0 (its high bit).</summary>
    public FfskBaud FfskTransparentBaud
    {
        get => (FfskBaud)(((Data[13] & 0x01) << 2) | ((Data[12] & 0xC0) >> 6));
        set
        {
            byte[] p = Data;
            int idx = (byte)value;
            p[12] = (byte)((p[12] & 0x3F) | ((idx & 0x03) << 6));
            p[13] = (byte)((p[13] & 0xFE) | ((idx >> 2) & 0x01));
        }
    }

    // ---- Audio tap block (record 0x3B/0) ------------------------------------------------

    private byte[] Audio => Image.Require(0x3B, 0).Data;

    /// <summary>RX tap-out point node number (low nibble of payload byte 3). R-nodes map directly:
    /// R1=1, R2=2, R4=4, R5=5, R7=7, R10=10.</summary>
    public int GetRxTapOutNode() => Audio[3] & 0x0F;

    /// <summary>Set the RX tap-out point node number.</summary>
    public void SetRxTapOutNode(int node)
    {
        if (node is < 0 or > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(node), node, "0..15");
        }

        Audio[3] = (byte)((Audio[3] & 0xF0) | (node & 0x0F));
    }

    /// <summary>EPTT1 tap-in point node number: payload[11] = 0x20 | (node &lt;&lt; 1). T3=3, T5=5,
    /// T8=8, T13=13.</summary>
    public int GetEptt1TapInNode() => (Audio[11] >> 1) & 0x0F;

    /// <summary>Set the EPTT1 tap-in point node number.</summary>
    public void SetEptt1TapInNode(int node)
    {
        if (node is < 0 or > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(node), node, "0..15");
        }

        Audio[11] = (byte)(0x20 | ((node & 0x0F) << 1));
    }

    /// <summary>RX tap-out unmute condition (bits [3:1] of payload byte 4). The CPS changes this
    /// automatically when the tap-out point changes, so revert it if you only meant the tap.</summary>
    public TapOutUnmute TapOutUnmute
    {
        get => (TapOutUnmute)(Audio[4] & 0x0E);
        set => Audio[4] = (byte)((Audio[4] & 0xF1) | ((byte)value & 0x0E));
    }
}
