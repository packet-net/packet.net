using System.Globalization;
using System.Text;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A decoded NinoTNC "TX-Test" diagnostic frame. The TNC emits one of these
/// only when the operator presses the on-board TX-Test button: the modem
/// transmits a test signal over the air, *and* sends this synthetic KISS
/// frame to its USB host carrying its firmware version, identity, and
/// runtime counters. It is on-demand only — the firmware does not emit it
/// on a timer. The frame is a regular KISS data frame (command 0x00) whose
/// payload is an ASCII run of <c>=Key:Value</c> pairs rather than an AX.25
/// frame.
/// </summary>
/// <remarks>
/// <para>
/// Frame body looks like this (verbatim, no separators between pairs):
/// </para>
/// <code>
/// =FirmwareVr:3.44=SerialNmbr:...=UptimeMilS:0001A2B3=BrdSwchMod:040F0023
/// =AX25RxPkts:0000007F=IL2PRxPkts:00000000=IL2PRxUnCr:00000000=TxPktCount:0000003E
/// =PreamblCnt:00000041=LoopCycles:000A28F2=LostADCSmp:00000000
/// </code>
/// <para>
/// Numeric fields are hex-encoded. <c>BrdSwchMod</c> packs four bytes:
/// <c>XX</c> (board revision), <c>YY</c> (DIP switch position, low 4 bits),
/// then <c>ZZZZ</c> (a 16-bit firmware mode value — its low byte is the
/// "running mode" lookup index in <see cref="NinoTncCatalog.FirmwareByteToMode"/>).
/// </para>
/// <para>
/// This parser is permissive: it scans for the <c>=FirmwareVr:</c> marker
/// rather than assuming a particular AX.25-shaped prefix, because firmware
/// emits the frame as a KISS data frame and the bytes before the marker
/// are not a real address header.
/// </para>
/// </remarks>
public sealed record NinoTncTxTestFrame
{
    /// <summary>
    /// Firmware version reported by the modem, parsed into Nino's two-
    /// component form (e.g. <c>3.44</c>). <c>null</c> if the firmware
    /// version field was missing or unparseable.
    /// </summary>
    public Firmware.NinoTncFirmwareVersion? FirmwareVersion { get; init; }

    /// <summary>
    /// The raw firmware version string the firmware emitted (e.g.
    /// <c>"3.44"</c>). Kept alongside <see cref="FirmwareVersion"/> so
    /// callers that need the verbatim string still have it. <c>null</c>
    /// if the field was missing.
    /// </summary>
    public string? FirmwareVersionRaw { get; init; }

    /// <summary>
    /// Which dsPIC chip variant this modem runs, derived from the
    /// firmware version's major component. Important for firmware
    /// update flows — the two variants need different hex images and
    /// mixing them up bricks the modem until ICSP recovery.
    /// </summary>
    public Firmware.NinoTncChipVariant ChipVariant =>
        FirmwareVersion?.ChipVariant ?? Firmware.NinoTncChipVariant.Unknown;

    /// <summary>Serial number string. <c>null</c> when the TNC has none set.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Uptime in milliseconds, decoded from <c>UptimeMilS</c>.</summary>
    public long? UptimeMs { get; init; }

    /// <summary>Uptime as a <see cref="TimeSpan"/>, when <see cref="UptimeMs"/> is set.</summary>
    public TimeSpan? Uptime => UptimeMs.HasValue ? TimeSpan.FromMilliseconds(UptimeMs.Value) : null;

    /// <summary>The XX byte from BrdSwchMod — the board revision number.</summary>
    public byte? BoardRevision { get; init; }

    /// <summary>The YY byte from BrdSwchMod — the DIP-switch position (0–15).</summary>
    public byte? DipSwitchPosition { get; init; }

    /// <summary>
    /// The low byte of the ZZZZ field from BrdSwchMod — the firmware-mode
    /// identifier that <see cref="NinoTncCatalog.FirmwareByteToMode"/> maps
    /// to the "actually running" mode (matters when DIP=15 = "Set from KISS").
    /// </summary>
    public byte? FirmwareModeByte { get; init; }

    /// <summary>
    /// The mode the TNC is currently running, resolved through
    /// <see cref="NinoTncCatalog.TryGetByFirmwareByte"/>. <c>null</c> if the
    /// firmware byte isn't in the catalog (firmware likely newer than ours).
    /// </summary>
    public NinoTncMode? RunningMode { get; init; }

    /// <summary>Count of received AX.25 packets since boot.</summary>
    public long? Ax25RxPackets { get; init; }

    /// <summary>Count of received IL2P packets since boot.</summary>
    public long? Il2pRxPackets { get; init; }

    /// <summary>Count of IL2P packets received with uncorrectable errors.</summary>
    public long? Il2pRxUncorrectable { get; init; }

    /// <summary>Count of transmitted packets since boot.</summary>
    public long? TxPacketCount { get; init; }

    /// <summary>Count of received preambles since boot.</summary>
    public long? PreambleCount { get; init; }

    /// <summary>Firmware main-loop cycle count since boot.</summary>
    public long? LoopCycles { get; init; }

    /// <summary>Count of dropped ADC samples since boot.</summary>
    public long? LostAdcSamples { get; init; }

    private const string Marker = "=FirmwareVr:";

    /// <summary>
    /// Try to parse a TX-Test frame out of a decoded KISS data frame.
    /// </summary>
    public static bool TryParse(KissFrame frame, out NinoTncTxTestFrame? parsed)
    {
        parsed = null;
        if (frame.Command != KissCommand.Data)
        {
            return false;
        }
        return TryParse(frame.Payload, out parsed);
    }

    /// <summary>
    /// Try to parse a TX-Test frame out of raw KISS-frame payload bytes (the
    /// bytes between the command byte and the closing FEND, post-unescape).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out NinoTncTxTestFrame? parsed)
    {
        parsed = null;
        int markerIndex = IndexOfAscii(payload, Marker);
        if (markerIndex < 0)
        {
            return false;
        }

        string ascii = Encoding.ASCII.GetString(payload[markerIndex..]);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ascii.Split('=', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = pair.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == pair.Length - 1)
            {
                continue;
            }
            string key = pair[..colon];
            string value = pair[(colon + 1)..];
            fields[key] = value;
        }

        byte? boardRev = null, dipPos = null, fwModeByte = null;
        NinoTncMode? running = null;
        if (fields.TryGetValue("BrdSwchMod", out var brdSwchMod) && brdSwchMod.Length >= 8)
        {
            if (TryParseHexByte(brdSwchMod.AsSpan(0, 2), out var xx))
            {
                boardRev = xx;
            }
            if (TryParseHexByte(brdSwchMod.AsSpan(2, 2), out var yy))
            {
                dipPos = (byte)(yy & 0x0F);
            }
            // ZZZZ is 4 hex chars — high byte then low byte of a 16-bit value.
            // The catalog keys on the low byte (kissproxy convention).
            if (TryParseHexByte(brdSwchMod.AsSpan(6, 2), out var lowZ))
            {
                fwModeByte = lowZ;
                running = NinoTncCatalog.TryGetByFirmwareByte(lowZ);
            }
        }

        parsed = new NinoTncTxTestFrame
        {
            FirmwareVersionRaw = fields.GetValueOrDefault("FirmwareVr"),
            FirmwareVersion = Firmware.NinoTncFirmwareVersion.TryParse(fields.GetValueOrDefault("FirmwareVr"), out var fwVersion) ? fwVersion : null,
            SerialNumber = NormaliseSerial(fields.GetValueOrDefault("SerialNmbr")),
            UptimeMs = TryHexLong(fields, "UptimeMilS"),
            BoardRevision = boardRev,
            DipSwitchPosition = dipPos,
            FirmwareModeByte = fwModeByte,
            RunningMode = running,
            Ax25RxPackets = TryHexLong(fields, "AX25RxPkts"),
            Il2pRxPackets = TryHexLong(fields, "IL2PRxPkts"),
            Il2pRxUncorrectable = TryHexLong(fields, "IL2PRxUnCr"),
            TxPacketCount = TryHexLong(fields, "TxPktCount"),
            PreambleCount = TryHexLong(fields, "PreamblCnt"),
            LoopCycles = TryHexLong(fields, "LoopCycles"),
            LostAdcSamples = TryHexLong(fields, "LostADCSmp"),
        };
        return true;
    }

    private static long? TryHexLong(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
        {
            return null;
        }
        return long.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> hex, out byte value) =>
        byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static string? NormaliseSerial(string? raw)
    {
        if (raw is null) return null;
        var cleaned = raw.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != (byte)needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }
}
