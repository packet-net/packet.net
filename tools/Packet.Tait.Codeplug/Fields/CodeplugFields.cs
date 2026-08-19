using System.Globalization;

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
    private const int ChTxInhibit = 82;    // 2 bits
    private const int ChSquelch = 84;      // 2 bits (RxBusyDetect)
    private const int ChTxSubType = 86;    // 2 bits (subaudible type)
    private const int ChRxSubType = 88;    // 2 bits
    private const int ChTxSubIndex = 90;   // 8 bits (tone-table index)
    private const int ChRxSubIndex = 98;   // 8 bits
    private const int ChNetwork = 106;     // 3 bits (ChannelNetworkId)
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

    /// <summary>Channel squelch / busy-detect tightness (the CPS "Squelch" column).</summary>
    public Squelch GetSquelch(int channel) => (Squelch)Ch(channel, ChSquelch, 2);

    /// <summary>Set channel squelch tightness.</summary>
    public void SetSquelch(int channel, Squelch value) => SetCh(channel, ChSquelch, 2, (long)value);

    /// <summary>Channel transmit inhibit.</summary>
    public TxInhibit GetTxInhibit(int channel) => (TxInhibit)Ch(channel, ChTxInhibit, 2);

    /// <summary>Set channel transmit inhibit.</summary>
    public void SetTxInhibit(int channel, TxInhibit value) => SetCh(channel, ChTxInhibit, 2, (long)value);

    /// <summary>Channel network reference (the CPS "Network" column), 0..7.</summary>
    public int GetNetwork(int channel) => (int)Ch(channel, ChNetwork, 3);

    /// <summary>Set the channel network reference (0..7).</summary>
    public void SetNetwork(int channel, int network) =>
        SetCh(channel, ChNetwork, 3, network is >= 0 and <= 7 ? network : throw new ArgumentOutOfRangeException(nameof(network), network, "0..7"));

    /// <summary>TX subaudible signalling type (None / CTCSS / DCS).</summary>
    public SubaudibleType GetTxSubaudibleType(int channel) => (SubaudibleType)Ch(channel, ChTxSubType, 2);

    /// <summary>Set the TX subaudible signalling type.</summary>
    public void SetTxSubaudibleType(int channel, SubaudibleType value) => SetCh(channel, ChTxSubType, 2, (long)value);

    /// <summary>RX subaudible signalling type (None / CTCSS / DCS).</summary>
    public SubaudibleType GetRxSubaudibleType(int channel) => (SubaudibleType)Ch(channel, ChRxSubType, 2);

    /// <summary>Set the RX subaudible signalling type.</summary>
    public void SetRxSubaudibleType(int channel, SubaudibleType value) => SetCh(channel, ChRxSubType, 2, (long)value);

    /// <summary>TX subaudible tone/code index into the radio's tone table.</summary>
    public int GetTxSubaudibleIndex(int channel) => (int)Ch(channel, ChTxSubIndex, 8);

    /// <summary>Set the TX subaudible tone/code index.</summary>
    public void SetTxSubaudibleIndex(int channel, int index) => SetCh(channel, ChTxSubIndex, 8, RequireByte(index));

    /// <summary>RX subaudible tone/code index into the radio's tone table.</summary>
    public int GetRxSubaudibleIndex(int channel) => (int)Ch(channel, ChRxSubIndex, 8);

    /// <summary>Set the RX subaudible tone/code index.</summary>
    public void SetRxSubaudibleIndex(int channel, int index) => SetCh(channel, ChRxSubIndex, 8, RequireByte(index));

    private static long RequireByte(int v) =>
        v is >= 0 and <= 255 ? v : throw new ArgumentOutOfRangeException(nameof(v), v, "0..255");

    // ---- Subaudible tone/code tables --------------------------------------------------
    //
    // A channel's subaudible index does not name a tone directly: it points into a small
    // per-codeplug table populated in insertion order. CTCSS frequencies live in record type 0x32
    // as 12-bit entries (frequency in tenths of a Hz); DCS codes live in record type 0x3D as 9-bit
    // entries (the octal code as its integer value). GetRx/TxSubaudible resolves a channel to the
    // actual tone.

    /// <summary>CTCSS frequencies (Hz) in the codeplug's tone table, indexed by subaudible index.</summary>
    public IReadOnlyList<double> CtcssTable => ReadTable(0x32, 12).Select(v => v / 10.0).ToList();

    /// <summary>DCS codes (as their 3-digit octal form) in the codeplug's code table.</summary>
    public IReadOnlyList<string> DcsTable =>
        ReadTable(0x3D, 9).Select(v => Convert.ToString(v, 8).PadLeft(3, '0')).ToList();

    /// <summary>Resolve a channel's RX subaudible signalling to a human string: <c>None</c>,
    /// <c>CTCSS 67.0</c>, or <c>DCS 017</c>.</summary>
    public string GetRxSubaudible(int channel) =>
        DescribeSubaudible(GetRxSubaudibleType(channel), GetRxSubaudibleIndex(channel));

    /// <summary>Resolve a channel's TX subaudible signalling to a human string.</summary>
    public string GetTxSubaudible(int channel) =>
        DescribeSubaudible(GetTxSubaudibleType(channel), GetTxSubaudibleIndex(channel));

    private string DescribeSubaudible(SubaudibleType type, int index)
    {
        switch (type)
        {
            case SubaudibleType.Ctcss:
                IReadOnlyList<double> ctcss = CtcssTable;
                return index < ctcss.Count
                    ? $"CTCSS {ctcss[index].ToString("0.0", CultureInfo.InvariantCulture)}"
                    : $"CTCSS #{index} (not in table)";
            case SubaudibleType.Dcs:
                IReadOnlyList<string> dcs = DcsTable;
                return index < dcs.Count ? $"DCS {dcs[index]}" : $"DCS #{index} (not in table)";
            default:
                return "None";
        }
    }

    /// <summary>Set a channel's RX subaudible to a CTCSS tone (Hz), adding it to the codeplug's
    /// tone table if needed.</summary>
    public void SetRxCtcss(int channel, double hz) => SetSubaudible(channel, rx: true, CtcssSlot(hz), SubaudibleType.Ctcss);

    /// <summary>Set a channel's TX subaudible to a CTCSS tone (Hz).</summary>
    public void SetTxCtcss(int channel, double hz) => SetSubaudible(channel, rx: false, CtcssSlot(hz), SubaudibleType.Ctcss);

    /// <summary>Set a channel's RX subaudible to a DCS code (its 3-digit octal form, e.g. "023").</summary>
    public void SetRxDcs(int channel, string code) => SetSubaudible(channel, rx: true, DcsSlot(code), SubaudibleType.Dcs);

    /// <summary>Set a channel's TX subaudible to a DCS code.</summary>
    public void SetTxDcs(int channel, string code) => SetSubaudible(channel, rx: false, DcsSlot(code), SubaudibleType.Dcs);

    /// <summary>
    /// Set the audio I/O (Programmable I/O -&gt; Audio) to the audio routing the amateur-packet community
    /// has settled on over time. This is a convention, not a CPS feature - the CPS knows nothing about
    /// packet radio; it is just a specific set of the item-59 audio fields: Rx row tap-out R1 (type
    /// D-Split, unmute Except-on-PTT), EPTT1 row tap-in T13 (type A-Bypass In, unmute On-PTT, tap-out
    /// None type C-Bypass Out), Mic PTT and EPTT2 at defaults, all inversion disabled. The audio-IO
    /// block is self-contained, so applying this record configures the routing regardless of the rest of
    /// the codeplug. The exact bytes were validated to reproduce a CPS save of that manual configuration.
    /// </summary>
    public void ApplyPacketAudioDefaults()
    {
        Image.RemoveRecordsInSection(0x3B);
        Image.SetRecord(new CodeplugRecord(0x3B, 0, (byte[])PacketAudioRecord.Clone()));
        SetItemCount(0x3B, PacketAudioEntryCount);
    }

    private const int PacketAudioEntryCount = 4;

    private static readonly byte[] PacketAudioRecord =
    {
        0x00, 0x01, 0x00, 0xC1, 0x08, 0x80, 0x00, 0x00, 0x40, 0x00,
        0x80, 0x3A, 0x00, 0x20, 0x00, 0x40, 0x00, 0x00, 0x10, 0x00,
    };

    /// <summary>Clear a channel's RX subaudible signalling.</summary>
    public void SetRxSubaudibleNone(int channel) => SetRxSubaudibleType(channel, SubaudibleType.None);

    /// <summary>Clear a channel's TX subaudible signalling.</summary>
    public void SetTxSubaudibleNone(int channel) => SetTxSubaudibleType(channel, SubaudibleType.None);

    private void SetSubaudible(int channel, bool rx, int slot, SubaudibleType type)
    {
        if (rx)
        {
            SetRxSubaudibleType(channel, type);
            SetRxSubaudibleIndex(channel, slot);
        }
        else
        {
            SetTxSubaudibleType(channel, type);
            SetTxSubaudibleIndex(channel, slot);
        }
    }

    private int CtcssSlot(double hz)
    {
        int value = (int)Math.Round(hz * 10, MidpointRounding.AwayFromZero);
        if (!ValidCtcssTimesTen.Contains(value))
        {
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "not a supported CTCSS tone");
        }

        return EnsureSlot(0x32, 12, 0x32, value);
    }

    private int DcsSlot(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        int value;
        try
        {
            value = Convert.ToInt32(code.Trim(), 8);
        }
        catch (FormatException)
        {
            throw new ArgumentException($"'{code}' is not a valid octal DCS code", nameof(code));
        }

        if (value is <= 0 or > 0x1FF)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "DCS code out of 9-bit octal range");
        }

        return EnsureSlot(0x3D, 9, 0x3D, value);
    }

    /// <summary>Find <paramref name="value"/> in the tone table; if absent, reuse a free (zero)
    /// slot or append a new entry, growing the table records and bumping the item count. Returns
    /// the slot index.</summary>
    private int EnsureSlot(byte section, int entryBits, byte itemId, int value)
    {
        var entries = ReadTable(section, entryBits).ToList();
        int at = entries.IndexOf(value);
        if (at >= 0)
        {
            return at;
        }

        at = entries.IndexOf(0);
        if (at >= 0)
        {
            entries[at] = value;
        }
        else
        {
            entries.Add(value);
            at = entries.Count - 1;
        }

        WriteTable(section, entryBits, entries);
        SetItemCount(itemId, entries.Count);
        return at;
    }

    private void WriteTable(byte section, int entryBits, List<int> entries)
    {
        var buf = new byte[((entries.Count * entryBits) + 7) / 8];
        for (int e = 0; e < entries.Count; e++)
        {
            for (int k = 0; k < entryBits; k++)
            {
                if (((entries[e] >> k) & 1) != 0)
                {
                    int bit = (e * entryBits) + k;
                    buf[bit >> 3] |= (byte)(1 << (bit & 7));
                }
            }
        }

        for (int rec = 0, off = 0; off < buf.Length; rec++, off += 32)
        {
            int len = Math.Min(32, buf.Length - off);
            Image.SetRecord(new CodeplugRecord(section, (byte)rec, buf[off..(off + len)]));
        }
    }

    private void SetItemCount(byte itemId, int count)
    {
        var records = Image.Records.Where(r => r.Section == 0x01).OrderBy(r => r.Index).ToList();
        byte[] concat = records.SelectMany(r => r.Data).ToArray();
        for (int off = 0; off + 7 <= concat.Length; off += 7)
        {
            if (concat[off] == itemId)
            {
                concat[off + 3] = (byte)(count & 0xFF);
                concat[off + 4] = (byte)((count >> 8) & 0xFF);
                int p = 0;
                foreach (CodeplugRecord r in records)
                {
                    Array.Copy(concat, p, r.Data, 0, r.Data.Length);
                    p += r.Data.Length;
                }

                return;
            }
        }

        throw new InvalidOperationException($"item 0x{itemId:X2} not found in the item index");
    }

    /// <summary>Supported CTCSS tone frequencies times ten (standard + non-standard).</summary>
    private static readonly HashSet<int> ValidCtcssTimesTen = new()
    {
        670, 693, 719, 744, 770, 797, 825, 854, 885, 915, 948, 974, 1000, 1035, 1072, 1109, 1148,
        1188, 1230, 1273, 1318, 1365, 1413, 1462, 1514, 1567, 1622, 1679, 1738, 1799, 1862, 1928,
        2035, 2107, 2181, 2257, 2336, 2418, 2503,
        1598, 1655, 1713, 1773, 1835, 1899, 1966, 1995, 2065, 2291, 2541,
    };

    private int[] ReadTable(byte section, int entryBits)
    {
        byte[] buf = Image.Records
            .Where(r => r.Section == section)
            .OrderBy(r => r.Index)
            .SelectMany(r => r.Data)
            .ToArray();
        int count = (buf.Length * 8) / entryBits;
        var entries = new int[count];
        for (int e = 0; e < count; e++)
        {
            int value = 0;
            for (int k = 0; k < entryBits; k++)
            {
                int bit = (e * entryBits) + k;
                if (((buf[bit >> 3] >> (bit & 7)) & 1) != 0)
                {
                    value |= 1 << k;
                }
            }

            entries[e] = value;
        }

        return entries;
    }

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

    /// <summary>RX tap-out audio inverted (payload byte 4 bit 0x40).</summary>
    public bool RxTapOutInverted
    {
        get => (Audio[4] & 0x40) != 0;
        set { if (value) { Audio[4] |= 0x40; } else { Audio[4] &= 0xBF; } }
    }

    /// <summary>EPTT1 tap-in audio inverted (payload byte 14 bit 0x08).</summary>
    public bool Eptt1TapInInverted
    {
        get => (Audio[14] & 0x08) != 0;
        set { if (value) { Audio[14] |= 0x08; } else { Audio[14] &= 0xF7; } }
    }
}
