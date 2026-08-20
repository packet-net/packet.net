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

    /// <summary>True when the image carries the given record (so an optional block's fields are present).</summary>
    public bool HasRecord(byte section, int index)
    {
        foreach (CodeplugRecord r in Image.Records)
        {
            if (r.Section == section && r.Index == index)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the GPS block (record 0x45/0) is present.</summary>
    public bool HasGps => HasRecord(0x45, 0);

    /// <summary>True when the customer-data global block (record 0x4C/0) is present.</summary>
    public bool HasCustomerData => HasRecord(0x4C, 0);

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

    /// <summary>Set channel transmit inhibit. The CPS greys this field when the channel's receive
    /// frequency is 0, so setting it there is refused.</summary>
    public void SetTxInhibit(int channel, TxInhibit value)
    {
        RequireAvailable(GetRxFrequencyHz(channel) != 0, "the channel's receive frequency is not 0");
        SetCh(channel, ChTxInhibit, 2, (long)value);
    }

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

    /// <summary>
    /// Upgrade the codeplug for the Packet.NET "pdn-basic" feature set: the CCDI command channel that
    /// carries the radio telemetry and control Packet.Radio.Tait uses - averaged and instantaneous RSSI,
    /// forward/reverse power, PA temperature, radio status/identity, transmitter keying, and the PROGRESS
    /// stream that carries carrier-sense (DCD) and external-PTT edges. It enables the CCDI master, keeps
    /// the radio in Command mode at power-up so it is always CCDI-reachable, turns on progress-message
    /// output (needed for DCD/PTT), and sets the command-mode baud to the Packet.NET default (28800).
    /// It does not touch the data port (that follows the physical wiring) or the RF config, so it is safe
    /// to layer onto a radio already configured for its channels. It changes only the data record (0x09).
    /// </summary>
    public void ApplyPdnBasic()
    {
        CcdiModeAllowed = true;
        PowerupState = DataPowerupMode.CommandMode;
        CcdiProgressMessageEnabled = true;
        CommandModeBaud = FfskBaud.Baud28800;
    }

    /// <summary>
    /// Upgrade the codeplug for the Packet.NET "pdn-extra" feature set: the TNC-less internal FFSK packet
    /// modem (the radio's own byte pipe, driven by <c>TaitTransparentTransport</c>) plus the SDM side
    /// channel used for out-of-band mode signalling. Includes everything <see cref="ApplyPdnBasic"/> does
    /// (the transparent transport is entered from Command mode and escapes back on teardown, so the radio
    /// must stay CCDI-reachable), and adds: transparent mode enabled; <b>ignore-escape-sequence OFF</b>
    /// (load-bearing - without it the escape can never return the radio to Command mode and it wedges);
    /// ignore-subaudible on the data path (so the modem is not gated by tone squelch); the transparent
    /// terminal baud (28800) and over-air FFSK baud (2400) at the Packet.NET defaults; and SDM plus
    /// CCDI SDM output for the mode-signalling side channel. It changes only the data record (0x09). The
    /// over-air FFSK baud must match at both ends of the link; adjust it (and the bauds) if your
    /// deployment differs. It configures the internal modem, not an external-TNC audio path - use the
    /// separate <see cref="ApplyPacketAudioDefaults"/> preset for a soundcard/TNC deployment.
    /// </summary>
    public void ApplyPdnExtra()
    {
        ApplyPdnBasic();
        TransparentModeEnabled = true;                 // must precede the transparent-gated fields
        IgnoreEscapeSequence = false;
        IgnoreSubaudibleOnData = true;
        FfskTransparentBaud = FfskBaud.Baud28800;
        FfskModemBaud = FfskModemRate.Baud2400;
        SdmEnabled = true;
        CcdiSdmOutputEnabled = true;
    }

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

    /// <summary>SDM (short data message) reception/transmission enabled (the CPS "SDM Enabled" checkbox,
    /// Data > SDM > All SDMs). Enabling it also cascades the CPS defaults for the text-SDM flags
    /// (bits 155..157), matching what the CPS writes.</summary>
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

    /// <summary>THSD (high-speed data) modem master enable (the CPS "THSD Modem Enabled" checkbox,
    /// Data > General > Transparent Mode). The HSD baud field is only meaningful when this is on.</summary>
    public bool ThsdModemEnabled
    {
        get => (Data[15] & 0x08) != 0;
        set
        {
            if (value)
            {
                RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled");
                Data[15] |= 0x08;
            }
            else
            {
                Data[15] &= 0xF7;
            }
        }
    }

    /// <summary>FFSK Transparent-mode (byte-pipe data operation) enabled (the CPS "Transparent Mode
    /// Enabled" checkbox, Data > General > Transparent Mode).</summary>
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

    /// <summary>Data-port routing (the CPS "Data Port", Data > Serial Communications; low two bits of
    /// payload byte 14).</summary>
    public DataPort DataPort
    {
        get => (DataPort)(Data[14] & 0x03);
        set => Data[14] = (byte)((Data[14] & 0xFC) | ((byte)value & 0x03));
    }

    /// <summary>FFSK transparent-mode terminal baud (the CPS "Baud Rate" FFSK Transparent Mode column,
    /// Data > Serial Communications), a 3-bit index split across payload[12] bits[7:6] (low two bits of
    /// the index) and payload[13] bit0 (its high bit).</summary>
    public FfskBaud FfskTransparentBaud
    {
        get => (FfskBaud)(((Data[13] & 0x01) << 2) | ((Data[12] & 0xC0) >> 6));
        set
        {
            RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled");
            byte[] p = Data;
            int idx = (byte)value;
            p[12] = (byte)((p[12] & 0x3F) | ((idx & 0x03) << 6));
            p[13] = (byte)((p[13] & 0xFE) | ((idx >> 2) & 0x01));
        }
    }

    /// <summary>Over-air FFSK modem data rate (the CPS "FFSK Baud Rate", Data > RF Modems > FFSK Modem),
    /// a 2-bit index in payload byte 21 bits[4:3]. This is the on-air symbol rate the modem runs and must
    /// match at both ends of a Transparent link; distinct from the terminal serial rate
    /// (<see cref="FfskTransparentBaud"/>).</summary>
    public FfskModemRate FfskModemBaud
    {
        get => (FfskModemRate)((Data[21] >> 3) & 0x03);
        set => Data[21] = (byte)((Data[21] & ~0x18) | (((byte)value & 0x03) << 3));
    }

    // ---- CCDI / SDM / transparent runtime-feature enablers (record 0x09/0) --------------
    //
    // These are the codeplug prerequisites the Packet.Radio.Tait runtime features depend on: CCDI
    // (RSSI / DCD / PTT / status) needs the radio in a CCDI-capable command mode, SDM reception needs
    // the messages routed out to the host, and the FFSK Transparent path has its own escape / mute
    // gotchas. Each field is one entry in the item-9 (record 0x09/0) bit-stream, which is packed in
    // schema field order; the bit offsets below are validated against real-radio CPS saves.

    // LSB-first bit access into a record payload.
    private static long GetBits(byte[] p, int bitOffset, int bitLength)
    {
        long value = 0;
        for (int k = 0; k < bitLength; k++)
        {
            int b = bitOffset + k;
            if (((p[b >> 3] >> (b & 7)) & 1) != 0)
            {
                value |= 1L << k;
            }
        }

        return value;
    }

    private static void SetBits(byte[] p, int bitOffset, int bitLength, long value)
    {
        for (int k = 0; k < bitLength; k++)
        {
            int b = bitOffset + k;
            int mask = 1 << (b & 7);
            p[b >> 3] = ((value >> k) & 1) != 0 ? (byte)(p[b >> 3] | mask) : (byte)(p[b >> 3] & ~mask);
        }
    }

    private long GetDataBits(int bitOffset, int bitLength) => GetBits(Data, bitOffset, bitLength);

    private void SetDataBits(int bitOffset, int bitLength, long value) => SetBits(Data, bitOffset, bitLength, value);

    // A Tait "identity" field is eight ASCII characters, seven bits each (56 bits). Trailing nulls
    // are unused character slots; the value is trimmed to its first null.
    private static string GetAscii7(byte[] p, int bitOffset)
    {
        Span<char> chars = stackalloc char[8];
        int n = 0;
        for (int i = 0; i < 8; i++)
        {
            int c = (int)GetBits(p, bitOffset + (7 * i), 7);
            if (c == 0)
            {
                break;
            }

            chars[n++] = (char)c;
        }

        return new string(chars[..n]);
    }

    private static void SetAscii7(byte[] p, int bitOffset, string value)
    {
        for (int i = 0; i < 8; i++)
        {
            char c = i < value.Length ? value[i] : '\0';
            SetBits(p, bitOffset + (7 * i), 7, c);
        }
    }

    // Mirror the CPS's "this field is only available if ..." rules: a field whose enabling condition is
    // not met is greyed in the CPS, so writing it would produce a codeplug state the UI cannot create.
    // Guarding the setter refuses that edit (the enabling field must be set first, exactly as in the UI).
    private static void RequireAvailable(bool condition, string requirement)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"this field is only available when {requirement} (as in the CPS).");
        }
    }

    // A Tait radio identity (the unit data identity and the GPS dispatcher address) is up to eight
    // characters from A-Z, 0-9, or the wildcard '*', which is what the CPS accepts.
    private static void ValidateRadioIdentity(string value)
    {
        if (value.Length > 8)
        {
            throw new ArgumentException("an identity is at most 8 characters", nameof(value));
        }

        foreach (char c in value)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '*'))
            {
                throw new ArgumentException("an identity uses A-Z, 0-9, or the wildcard '*'", nameof(value));
            }
        }
    }

    /// <summary>Master switch that lets the radio drop into CCDI command mode (the CPS "CCDI Mode
    /// Allowed" checkbox, Data > General > Command Mode; bit 177). Every CCDI runtime feature - RSSI,
    /// DCD / channel-busy, PTT control, status queries - needs this on; with it off the radio never
    /// presents the CCDI command channel.</summary>
    public bool CcdiModeAllowed
    {
        get => GetDataBits(177, 1) != 0;
        set => SetDataBits(177, 1, value ? 1 : 0);
    }

    /// <summary>Which data mode the radio powers up in (the CPS "Powerup State", Data > General; bits
    /// 17..18 - the XON/XOFF character fields ahead of it are a byte each, so it sits higher than a
    /// naive field-order count suggests). CCDI features need a command-capable power-up state; the
    /// transparent modes bring the radio straight up as a byte pipe.</summary>
    public DataPowerupMode PowerupState
    {
        get => (DataPowerupMode)GetDataBits(17, 2);
        set
        {
            // The CPS offers the FFSK / THSD Transparent power-up options only when the matching mode is on.
            if (value == DataPowerupMode.FfskTransparent)
            {
                RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled");
            }
            else if (value == DataPowerupMode.ThsdTransparent)
            {
                RequireAvailable(ThsdModemEnabled, "the THSD modem is enabled");
            }

            SetDataBits(17, 2, (byte)value);
        }
    }

    /// <summary>CCDI command-mode serial baud (the CPS "Baud Rate" Command Mode column, Data > Serial
    /// Communications; bits 97..99), the rate the host talks CCDI over the data port. A 3-bit index
    /// into the shared baud table.</summary>
    public FfskBaud CommandModeBaud
    {
        get => (FfskBaud)GetDataBits(97, 3);
        set => SetDataBits(97, 3, (byte)value);
    }

    /// <summary>THSD (high-speed data) modem baud (the CPS "Baud Rate" THSD Transparent Mode column,
    /// Data > Serial Communications; bits 107..109), a 3-bit index into the shared baud table.
    /// Meaningful when <see cref="ThsdModemEnabled"/> is on.</summary>
    public FfskBaud HsdBaud
    {
        get => (FfskBaud)GetDataBits(107, 3);
        set
        {
            RequireAvailable(ThsdModemEnabled, "the THSD modem is enabled");
            SetDataBits(107, 3, (byte)value);
        }
    }

    /// <summary>Route received SDMs out to the CCDI host (the CPS "Output SDMs Automatically" checkbox,
    /// Data > General > Command Mode; bit 151). Needed for a host to see incoming short data messages
    /// over the CCDI channel.</summary>
    public bool CcdiSdmOutputEnabled
    {
        get => GetDataBits(151, 1) != 0;
        set => SetDataBits(151, 1, value ? 1 : 0);
    }

    /// <summary>Emit CCDI progress / result messages to the host (the CPS "Output Progress Messages"
    /// checkbox, Data > General > Command Mode; bit 149). Lets a host see command acknowledgements.</summary>
    public bool CcdiProgressMessageEnabled
    {
        get => GetDataBits(149, 1) != 0;
        set => SetDataBits(149, 1, value ? 1 : 0);
    }

    /// <summary>Deliver SDMs to the host as text only, suppressing the binary form (the CPS "CCDI SDM
    /// Text Only" checkbox, Data > General > Command Mode; bit 170).</summary>
    public bool CcdiSdmTextOnly
    {
        get => GetDataBits(170, 1) != 0;
        set
        {
            if (value) { RequireAvailable(CcdiSdmOutputEnabled, "Output SDMs Automatically is on"); }
            SetDataBits(170, 1, value ? 1 : 0);
        }
    }

    /// <summary>Show the received-SDM indicator (the CPS "Indicate When SDM Received" checkbox, Data >
    /// SDM > Text SDMs Only; bit 155). Part of the text-SDM behaviour group that <see cref="SdmEnabled"/>
    /// defaults on when SDM is enabled.</summary>
    public bool TextSdmIndicator
    {
        get => GetDataBits(155, 1) != 0;
        set => SetDataBits(155, 1, value ? 1 : 0);
    }

    /// <summary>Auto-acknowledge transmitted text SDMs (the CPS "Transmit SDM Auto Acknowledgement"
    /// checkbox, Data > SDM > Text SDMs Only; bit 156).</summary>
    public bool TextSdmAutoAckTransmission
    {
        get => GetDataBits(156, 1) != 0;
        set
        {
            if (value) { RequireAvailable(SdmEnabled, "SDM is enabled"); }
            SetDataBits(156, 1, value ? 1 : 0);
        }
    }

    /// <summary>Auto-acknowledge received text SDMs (the CPS "Receive SDM Auto Acknowledgement"
    /// checkbox, Data > SDM > Text SDMs Only; bit 157).</summary>
    public bool TextSdmAutoAckReception
    {
        get => GetDataBits(157, 1) != 0;
        set
        {
            if (value) { RequireAvailable(SdmEnabled, "SDM is enabled"); }
            SetDataBits(157, 1, value ? 1 : 0);
        }
    }

    /// <summary>SDM auto-acknowledge delay in milliseconds (the CPS "SDM Auto Acknowledge Delay", Data >
    /// SDM > Text SDMs Only; bits 87..92, a 6-bit count of 100 ms steps, 0..5000 ms).</summary>
    public int SdmAutoAckDelayMs
    {
        get => (int)GetDataBits(87, 6) * 100;
        set
        {
            RequireAvailable(SdmEnabled && TextSdmAutoAckTransmission, "SDM and Transmit SDM Auto Acknowledgement are on");
            if (value is < 0 or > 5000 || value % 100 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..5000 ms in 100 ms steps");
            }

            SetDataBits(87, 6, value / 100);
        }
    }

    /// <summary>SDM wait-for-acknowledge time in seconds (the CPS "SDM Wait For Acknowledgement Time",
    /// Data > SDM > Text SDMs Only; bits 93..96, 1..15 s). The CPS only lets you edit this box when
    /// "Receive SDM Auto Acknowledgement" is on, but the value is stored regardless.</summary>
    public int SdmWaitForAck
    {
        get => (int)GetDataBits(93, 4);
        set
        {
            RequireAvailable(SdmEnabled && TextSdmAutoAckReception, "SDM and Receive SDM Auto Acknowledgement are on");
            if (value is < 1 or > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "1..15");
            }

            SetDataBits(93, 4, value);
        }
    }

    /// <summary>Ignore the FFSK Transparent-mode escape sequence (the CPS "Ignore Escape Sequence"
    /// checkbox, Data > General > Transparent Mode; bit 114). For a raw byte pipe this must be OFF,
    /// otherwise the escape bytes are swallowed and the link wedges; it is the classic TNC-less-Tait gotcha.</summary>
    public bool IgnoreEscapeSequence
    {
        get => GetDataBits(114, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled"); }
            SetDataBits(114, 1, value ? 1 : 0);
        }
    }

    /// <summary>Ignore subaudible (CTCSS / DCS) signalling on the data path (the CPS "Ignore DCS/CTCSS"
    /// checkbox, Data > RF Modems > FFSK Modem; bit 85). For a transparent data link both ends generally
    /// set this so the modem is not gated by tone squelch.</summary>
    public bool IgnoreSubaudibleOnData
    {
        get => GetDataBits(85, 1) != 0;
        set => SetDataBits(85, 1, value ? 1 : 0);
    }

    // ---- Remaining Data-form (item 9) fields, by CPS tab --------------------------------
    //
    // The rest of the Data form's controls, all in the same item-9 bit-stream and all validated
    // against a real-radio CPS save. The stored record is 32 bytes (256 bits); fields past bit 245
    // (the TOTAL MTU / retries / timeout / busy-backoff / channel-access tail) are trimmed off and the
    // CPS fills schema defaults, so they are not exposed here.

    // -- General tab --

    /// <summary>Open the monitor (mute override) on a dialled call (the CPS "Open Monitor On Dialled
    /// Call" checkbox, Data > General > Command Mode; bit 173).</summary>
    public bool OpenMonitorOnDialledCall
    {
        get => GetDataBits(173, 1) != 0;
        set => SetDataBits(173, 1, value ? 1 : 0);
    }

    /// <summary>Output all selcall receptions to the host (the CPS "Output All Selcall Receptions"
    /// checkbox, Data > General > Command Mode; bit 150).</summary>
    public bool SelcallOutputEnabled
    {
        get => GetDataBits(150, 1) != 0;
        set => SetDataBits(150, 1, value ? 1 : 0);
    }

    /// <summary>Maximum initial FFSK frame length flag (the CPS "Maximum Initial Frame Length" checkbox,
    /// Data > General > Transparent Mode; bit 160).</summary>
    public bool MaximumInitialFrameLength
    {
        get => GetDataBits(160, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled"); }
            SetDataBits(160, 1, value ? 1 : 0);
        }
    }

    /// <summary>Transparent-mode UART write delay in ms (the CPS "UART Write Delay", Data > General >
    /// Transparent Mode; bits 161..169, 0..500).</summary>
    public int UartWriteDelayMs
    {
        get => (int)GetDataBits(161, 9);
        set
        {
            if (value is < 0 or > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..500");
            }

            SetDataBits(161, 9, value);
        }
    }

    /// <summary>Transmit back-off time minimum in ms (the CPS "Tx Back-off Time (Min)", Data > General >
    /// Transparent Mode; bits 178..186, 0..500).</summary>
    public int TxBackoffTimeMinMs
    {
        get => (int)GetDataBits(178, 9);
        set
        {
            if (value is < 0 or > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..500");
            }

            SetDataBits(178, 9, value);
        }
    }

    /// <summary>Transmit back-off time maximum in ms (the CPS "Tx Back-off Time (Max)", Data > General >
    /// Transparent Mode; bits 187..196, 0..1000). The CPS requires the maximum to be greater than the
    /// minimum (or both 0 to disable the back-off).</summary>
    public int TxBackoffTimeMaxMs
    {
        get => (int)GetDataBits(187, 10);
        set
        {
            if (value is < 0 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..1000");
            }

            if (value != 0 && value <= TxBackoffTimeMinMs)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"must be greater than the minimum ({TxBackoffTimeMinMs} ms), or 0 to disable");
            }

            SetDataBits(187, 10, value);
        }
    }

    // -- Serial Communications tab --

    /// <summary>Software-flow-control XON character code (the CPS "XON Character", Data > Serial
    /// Communications; bits 1..8, a byte - the CPS shows it in hex, e.g. 0x11 = DC1).</summary>
    public byte XonCharacter
    {
        get => (byte)GetDataBits(1, 8);
        set => SetDataBits(1, 8, value);
    }

    /// <summary>Software-flow-control XOFF character code (the CPS "XOFF Character", Data > Serial
    /// Communications; bits 9..16, a byte - the CPS shows it in hex, e.g. 0x13 = DC3).</summary>
    public byte XoffCharacter
    {
        get => (byte)GetDataBits(9, 8);
        set => SetDataBits(9, 8, value);
    }

    /// <summary>Command-mode serial flow control (the CPS "Flow Control" Command Mode column, Data >
    /// Serial Communications; bits 100..101).</summary>
    public DataFlowControl CommandModeFlowControl
    {
        get => (DataFlowControl)GetDataBits(100, 2);
        set => SetDataBits(100, 2, (byte)value);
    }

    /// <summary>FFSK transparent-mode serial flow control (the CPS "Flow Control" FFSK Transparent Mode
    /// column, Data > Serial Communications; bits 105..106).</summary>
    public DataFlowControl FfskTransparentFlowControl
    {
        get => (DataFlowControl)GetDataBits(105, 2);
        set
        {
            RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled");
            SetDataBits(105, 2, (byte)value);
        }
    }

    /// <summary>THSD transparent-mode serial flow control (the CPS "Flow Control" THSD Transparent Mode
    /// column, Data > Serial Communications; bits 110..111).</summary>
    public DataFlowControl HsdFlowControl
    {
        get => (DataFlowControl)GetDataBits(110, 2);
        set
        {
            RequireAvailable(ThsdModemEnabled, "the THSD modem is enabled");
            SetDataBits(110, 2, (byte)value);
        }
    }

    // -- RF Modems tab --

    /// <summary>Check the FFSK packet length (the CPS "Check Packet Length" checkbox, Data > RF Modems >
    /// FFSK Modem; bit 158).</summary>
    public bool CheckPacketLength
    {
        get => GetDataBits(158, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled, "Transparent Mode is enabled"); }
            SetDataBits(158, 1, value ? 1 : 0);
        }
    }

    /// <summary>Mute the receiver while receiving FFSK (the CPS "FFSK Tone Blanking" checkbox, Data >
    /// RF Modems > FFSK Modem; bit 126).</summary>
    public bool FfskToneBlanking
    {
        get => GetDataBits(126, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled || SdmEnabled, "Transparent Mode or SDM is enabled"); }
            SetDataBits(126, 1, value ? 1 : 0);
        }
    }

    /// <summary>FFSK lead-in delay in ms (the CPS "FFSK Lead-In Delay", Data > RF Modems > FFSK Modem;
    /// bits 75..84, a 10-bit count in 5 ms steps, 15..5100 ms).</summary>
    public int FfskLeadInDelayMs
    {
        get => (int)GetDataBits(75, 10) * 5;
        set
        {
            if (value is < 15 or > 5100 || value % 5 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "15..5100 ms in 5 ms steps");
            }

            SetDataBits(75, 10, value / 5);
        }
    }

    /// <summary>FFSK lead-out delay in ms (the CPS "FFSK Lead-Out Delay", Data > RF Modems > FFSK Modem;
    /// bits 115..122, 0..250).</summary>
    public int FfskLeadOutDelayMs
    {
        get => (int)GetDataBits(115, 8);
        set
        {
            if (value is < 0 or > 250)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..250");
            }

            SetDataBits(115, 8, value);
        }
    }

    /// <summary>THSD wideband (Tait wideband) modem enable (the CPS "Wide Band Modem Enabled" checkbox,
    /// Data > RF Modems > THSD Modem; bit 154).</summary>
    public bool WidebandModemEnabled
    {
        get => GetDataBits(154, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled"); }
            SetDataBits(154, 1, value ? 1 : 0);
        }
    }

    /// <summary>THSD layer-2 protocol (the CPS "Layer 2 Protocol" combo, Data > RF Modems > THSD Modem;
    /// bits 124..125).</summary>
    public ThsdLayer2 ThsdLayer2Protocol
    {
        get => (ThsdLayer2)GetDataBits(124, 2);
        set
        {
            RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled");
            SetDataBits(124, 2, (byte)value);
        }
    }

    /// <summary>THSD forward error correction enable (the CPS "Forward Error Correction (FEC)" checkbox,
    /// Data > RF Modems > THSD Modem; bit 127).</summary>
    public bool ThsdForwardErrorCorrection
    {
        get => GetDataBits(127, 1) != 0;
        set
        {
            if (value) { RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled"); }
            SetDataBits(127, 1, value ? 1 : 0);
        }
    }

    /// <summary>THSD FEC codeword count (the CPS "Number of Blocks", Data > RF Modems > THSD Modem;
    /// bits 174..176, 1..7).</summary>
    public int ThsdNumberOfBlocks
    {
        get => (int)GetDataBits(174, 3);
        set
        {
            RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled");
            if (value is < 1 or > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "1..7");
            }

            SetDataBits(174, 3, value);
        }
    }

    /// <summary>THSD lead-in delay in ms (the CPS "THSD Lead-In Delay", Data > RF Modems > THSD Modem;
    /// bits 128..140, 0..5000).</summary>
    public int ThsdLeadInDelayMs
    {
        get => (int)GetDataBits(128, 13);
        set
        {
            RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled");
            if (value is < 0 or > 5000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..5000");
            }

            SetDataBits(128, 13, value);
        }
    }

    /// <summary>THSD lead-out delay in ms (the CPS "THSD Lead-Out Delay", Data > RF Modems > THSD Modem;
    /// bits 141..148, 0..250).</summary>
    public int ThsdLeadOutDelayMs
    {
        get => (int)GetDataBits(141, 8);
        set
        {
            RequireAvailable(TransparentModeEnabled && ThsdModemEnabled, "Transparent Mode and the THSD modem are enabled");
            if (value is < 0 or > 250)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..250");
            }

            SetDataBits(141, 8, value);
        }
    }

    // -- SDM tab --

    /// <summary>Allow a received SDM to overwrite a full buffer (the CPS "SDM Buffer Overwrite" checkbox,
    /// Data > SDM > All SDMs; bit 159).</summary>
    public bool SdmBufferOverwrite
    {
        get => GetDataBits(159, 1) != 0;
        set
        {
            if (value) { RequireAvailable(!CcdiSdmOutputEnabled, "Output SDMs Automatically is off"); }
            SetDataBits(159, 1, value ? 1 : 0);
        }
    }

    /// <summary>SDM caller-ID enable (the CPS "SDM Caller ID" checkbox, Data > SDM > Text SDMs Only).
    /// The CPS keeps the encode and decode bits (152 and 153) equal, so this drives both.</summary>
    public bool SdmCallerId
    {
        get => GetDataBits(152, 1) != 0;
        set
        {
            SetDataBits(152, 1, value ? 1 : 0);
            SetDataBits(153, 1, value ? 1 : 0);
        }
    }

    /// <summary>Unit data identity (the CPS "Unit Data Identity", Data > SDM > All SDMs; bits 19..74).
    /// Up to eight characters from A-Z, 0-9, or the wildcard '*'; blank ("") is an all-zero field.</summary>
    public string UnitDataIdentity
    {
        get => GetAscii7(Data, 19);
        set
        {
            ValidateRadioIdentity(value);
            SetAscii7(Data, 19, value);
        }
    }

    // -- TOTAL Transparent Mode tab (the fields that fall within the stored record) --

    /// <summary>TOTAL service class (the CPS "TOTAL Service" combo, Data > TOTAL Transparent Mode;
    /// bit 197).</summary>
    public TotalModeService TotalService
    {
        get => (TotalModeService)GetDataBits(197, 1);
        set
        {
            RequireAvailable(ThsdLayer2Protocol == ThsdLayer2.Total, "the Layer 2 Protocol is TOTAL");
            SetDataBits(197, 1, (byte)value);
        }
    }

    /// <summary>TOTAL radio ID (the CPS "Radio ID", Data > TOTAL Transparent Mode; bits 198..213,
    /// 0..65535).</summary>
    public int TotalRadioId
    {
        get => (int)GetDataBits(198, 16);
        set
        {
            RequireAvailable(ThsdLayer2Protocol == ThsdLayer2.Total, "the Layer 2 Protocol is TOTAL");
            if (value is < 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..65535");
            }

            SetDataBits(198, 16, value);
        }
    }

    /// <summary>TOTAL system ID (the CPS "System ID", Data > TOTAL Transparent Mode; bits 214..221,
    /// 0..255).</summary>
    public int TotalSystemId
    {
        get => (int)GetDataBits(214, 8);
        set
        {
            RequireAvailable(ThsdLayer2Protocol == ThsdLayer2.Total, "the Layer 2 Protocol is TOTAL");
            if (value is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..255");
            }

            SetDataBits(214, 8, value);
        }
    }

    /// <summary>TOTAL destination ID (the CPS "Destination ID", Data > TOTAL Transparent Mode; bits
    /// 222..237, 0..65535; the default is 0xFFFF).</summary>
    public int TotalDestinationId
    {
        get => (int)GetDataBits(222, 16);
        set
        {
            RequireAvailable(ThsdLayer2Protocol == ThsdLayer2.Total, "the Layer 2 Protocol is TOTAL");
            if (value is < 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..65535");
            }

            SetDataBits(222, 16, value);
        }
    }

    /// <summary>TOTAL link ID (the CPS "Link ID", Data > TOTAL Transparent Mode; bits 238..245,
    /// 0..255).</summary>
    public int TotalLinkId
    {
        get => (int)GetDataBits(238, 8);
        set
        {
            RequireAvailable(ThsdLayer2Protocol == ThsdLayer2.Total, "the Layer 2 Protocol is TOTAL");
            if (value is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..255");
            }

            SetDataBits(238, 8, value);
        }
    }

    // ---- GPS tab (record 0x45/0) --------------------------------------------------------
    //
    // The CPS Data form's GPS tab is a separate record from the item-9 data block. Bit offsets are
    // an LSB-first stream, validated against a real-radio GPS-enabled CPS save.

    private byte[] Gps => Image.Require(0x45, 0).Data;

    /// <summary>GPS position reporting enabled (the CPS "GPS Position Reporting Enabled" checkbox,
    /// Data > GPS > General; bit 136). The CPS only lets this be enabled when SDM is on, so enabling it
    /// here with SDM off throws, matching that rule.</summary>
    public bool GpsEnabled
    {
        get => GetBits(Gps, 136, 1) != 0;
        set
        {
            if (value && !SdmEnabled)
            {
                throw new InvalidOperationException("GPS position reporting requires SDM to be enabled first.");
            }

            SetBits(Gps, 136, 1, value ? 1 : 0);
        }
    }

    /// <summary>GPS serial port (the CPS "GPS Port", Data > GPS > Serial Communications; bits 0..1).</summary>
    public DataPort GpsSerialPort
    {
        get => (DataPort)GetBits(Gps, 0, 2);
        set => SetBits(Gps, 0, 2, (byte)value);
    }

    /// <summary>GPS serial baud rate (the CPS "Baud Rate", Data > GPS > Serial Communications; bits
    /// 2..5, a 4-bit index into the shared 1200..28800 baud table).</summary>
    public FfskBaud GpsBaudRate
    {
        get => (FfskBaud)GetBits(Gps, 2, 4);
        set => SetBits(Gps, 2, 4, (byte)value);
    }

    /// <summary>Poll-response channel type (the CPS "Poll Response Channel Type", Data > GPS > Channel
    /// Setup; bit 137).</summary>
    public GpsPollResponseChannelType GpsPollResponseChannelType
    {
        get => (GpsPollResponseChannelType)GetBits(Gps, 137, 1);
        set => SetBits(Gps, 137, 1, (byte)value);
    }

    /// <summary>Dedicated poll-response channel number (the CPS "Channel", Data > GPS > Channel Setup;
    /// bits 6..12). Only used when the channel type is Dedicated. 0 is "None"; otherwise it must be an
    /// existing channel (1.. the codeplug's channel count), which is what the CPS enforces.</summary>
    public int GpsPollResponseChannel
    {
        get => (int)GetBits(Gps, 6, 7);
        set
        {
            RequireAvailable(GpsPollResponseChannelType == GpsPollResponseChannelType.Dedicated,
                "the Poll Response Channel Type is Dedicated");
            if (value < 0 || value > ChannelCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"0 (None) or an existing channel 1..{ChannelCount}");
            }

            SetBits(Gps, 6, 7, value);
        }
    }

    /// <summary>Alarm callout interval in seconds (the CPS "Callout Interval", Data > GPS > Alarm Mode;
    /// bits 13..18, a 6-bit count in 5-second steps, 0..300 s).</summary>
    public int GpsCalloutIntervalSeconds
    {
        get => (int)GetBits(Gps, 13, 6) * 5;
        set
        {
            if (value is < 0 or > 300 || value % 5 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..300 s in 5 s steps");
            }

            SetBits(Gps, 13, 6, value / 5);
        }
    }

    /// <summary>Maximum number of alarm callouts (the CPS "Maximum Number of Callouts", Data > GPS >
    /// Alarm Mode; bits 19..26, 0..250).</summary>
    public int GpsMaxNumberOfCallouts
    {
        get => (int)GetBits(Gps, 19, 8);
        set
        {
            if (value is < 0 or > 250)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..250");
            }

            SetBits(Gps, 19, 8, value);
        }
    }

    /// <summary>Connection time-out in seconds (the CPS "Connection Time Out", Data > GPS > General;
    /// bits 30..37, a byte in 20-second steps, 20..600 s).</summary>
    public int GpsConnectionTimeoutSeconds
    {
        get => (int)GetBits(Gps, 30, 8) * 20;
        set
        {
            if (value is < 20 or > 600 || value % 20 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "20..600 s in 20 s steps");
            }

            SetBits(Gps, 30, 8, value / 20);
        }
    }

    /// <summary>GPS SDM lead-in delay in ms (the CPS "GPS Lead-In Delay", Data > GPS > RF Modems; bits
    /// 38..45, a byte in 5 ms steps, 0..1200 ms).</summary>
    public int GpsLeadInDelayMs
    {
        get => (int)GetBits(Gps, 38, 8) * 5;
        set
        {
            if (value is < 0 or > 1200 || value % 5 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..1200 ms in 5 ms steps");
            }

            SetBits(Gps, 38, 8, value / 5);
        }
    }

    /// <summary>Poll-response delay in ms (the CPS "Poll Response Delay Time", Data > GPS > RF Modems;
    /// bits 138..146, a 9-bit count in 10 ms steps, 0..5000 ms).</summary>
    public int GpsPollResponseDelayMs
    {
        get => (int)GetBits(Gps, 138, 9) * 10;
        set
        {
            if (value is < 0 or > 5000 || value % 10 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "0..5000 ms in 10 ms steps");
            }

            SetBits(Gps, 138, 9, value / 10);
        }
    }

    /// <summary>Send position on an emergency callout (the CPS "Send Position on Emergency Callout"
    /// checkbox, Data > GPS > Emergency; bit 49).</summary>
    public bool GpsSendOnEmergencyCallout
    {
        get => GetBits(Gps, 49, 1) != 0;
        set => SetBits(Gps, 49, 1, value ? 1 : 0);
    }

    /// <summary>AVL dispatcher address (the CPS "Dispatcher Address", Data > GPS > General; bits
    /// 50..105, default "00000000"). Like a <see cref="UnitDataIdentity"/>: up to eight characters from
    /// A-Z, 0-9, or the wildcard '*'.</summary>
    public string GpsDispatcherAddress
    {
        get => GetAscii7(Gps, 50);
        set
        {
            ValidateRadioIdentity(value);
            SetAscii7(Gps, 50, value);
        }
    }

    // ---- Customer Data tab (records 0x4C/0 and 0x4D/n) ----------------------------------
    //
    // Four global bytes plus a per-network table of four bytes each. Record 0x4C/0 carries four
    // leading pad bytes then the four global bytes; each network is record 0x4D at that network index.

    private byte[] CustomerGlobal => Image.Require(0x4C, 0).Data;

    /// <summary>The four global customer-data bytes (the CPS "Global Byte 1..4", Data > Customer Data).
    /// Stored after four leading pad bytes in record 0x4C/0.</summary>
    public byte GetCustomerGlobalByte(int index)
    {
        if (index is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "1..4");
        }

        return CustomerGlobal[3 + index];
    }

    /// <summary>Set one of the four global customer-data bytes (1..4).</summary>
    public void SetCustomerGlobalByte(int index, byte value)
    {
        if (index is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "1..4");
        }

        CustomerGlobal[3 + index] = value;
    }

    /// <summary>The four customer-data bytes for a network (the CPS "Network" table's "Byte 1..4",
    /// Data > Customer Data). Network N is record 0x4D at index N-1.</summary>
    public byte GetCustomerNetworkByte(int network, int index)
    {
        if (index is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "1..4");
        }

        if (network is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(network), network, "1..256");
        }

        return Image.Require(0x4D, (byte)(network - 1)).Data[index - 1];
    }

    /// <summary>Set one of a network's four customer-data bytes (index 1..4).</summary>
    public void SetCustomerNetworkByte(int network, int index, byte value)
    {
        if (index is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "1..4");
        }

        if (network is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(network), network, "1..256");
        }

        Image.Require(0x4D, (byte)(network - 1)).Data[index - 1] = value;
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
