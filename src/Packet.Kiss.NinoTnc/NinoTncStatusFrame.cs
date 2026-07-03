using System.Globalization;
using System.Text;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The NinoTNC's numeric diagnostic-register report: a fake UI frame whose
/// info text is a run of <c>=II:VALUE</c> fields, where <c>II</c> is a
/// two-hex-digit register index. This is the NUMERIC sibling of the
/// labelled <c>=FirmwareVr:</c> diagnostic that
/// <see cref="NinoTncTxTestFrame"/> parses.
/// </summary>
/// <remarks>
/// <para>
/// Bench-observed on firmware 3.41 (2026-07-02): the report is emitted
/// spontaneously once per minute (uptime deltas between consecutive frames
/// are exactly 60 000 ms — the interval SETBCNINT adjusts, see
/// <see cref="NinoTncCommands.BuildSetBeaconIntervalPayload"/>) as a KISS
/// Data frame carrying a fake AX.25 UI header <c>TNC&gt;USB</c>. Newer
/// firmware documents it addressed to <c>IDENT</c> and as the direct GETALL
/// response; on 3.41 GETALL answers with the labelled diagnostic instead
/// (which <see cref="FromDiagnostic"/> maps into this shape). The parser
/// ignores the header entirely and scans for the <c>=00:</c> marker.
/// </para>
/// <para>
/// Field encodings differ per register: register 00 is the plain-ASCII
/// firmware version (e.g. <c>3.41</c>), register 01 is eight <em>raw</em>
/// bytes (the KAUP8R identity register — all-zero when unset), and every
/// other register is eight uppercase hex digits. Verbatim capture from
/// firmware 3.41:
/// </para>
/// <code>
/// =00:3.41=01:········=02:00AC8F08=03:00000004=04:0000000F=06:00000002
/// =07:0000000D=08:00000004=09:00000000=0A:00000049=0B:00000016=0C:0483CA82
/// =0D:0000F4F6=0E:00014DBA=0F:000001E3=10:00000CE4=11:00000000
/// </code>
/// <para>
/// (Register 05 is absent on this firmware.) Unknown registers are kept in
/// <see cref="RawRegisters"/> so newer-firmware additions are not lost.
/// </para>
/// </remarks>
public sealed record NinoTncStatusFrame
{
    /// <summary>The register index carrying the plain-ASCII firmware version.</summary>
    public const byte FirmwareVersionRegister = 0x00;

    /// <summary>The register index carrying the raw 8-byte KAUP8R identity value.</summary>
    public const byte SerialNumberRegister = 0x01;

    /// <summary>
    /// Raw firmware version string from register 00 (e.g. <c>"3.41"</c>).
    /// <c>null</c> if the register was missing.
    /// </summary>
    public string? FirmwareVersionRaw { get; init; }

    /// <summary>
    /// Firmware version parsed into Nino's two-component form, or <c>null</c>
    /// when missing/unparseable.
    /// </summary>
    public Firmware.NinoTncFirmwareVersion? FirmwareVersion { get; init; }

    /// <summary>
    /// Serial-number / identity string from register 01 (the firmware's
    /// KAUP8R register). <c>null</c> when unset (all zero bytes).
    /// </summary>
    public string? SerialNumber { get; init; }

    /// <summary>Register 02 — uptime in milliseconds.</summary>
    public long? UptimeMs { get; init; }

    /// <summary>Uptime as a <see cref="TimeSpan"/>, when <see cref="UptimeMs"/> is set.</summary>
    public TimeSpan? Uptime => UptimeMs.HasValue ? TimeSpan.FromMilliseconds(UptimeMs.Value) : null;

    /// <summary>Register 03 — the board id / revision number.</summary>
    public long? BoardId { get; init; }

    /// <summary>
    /// Register 04 — the DIP switch positions, low four bits. <c>0b1111</c>
    /// (15) means all four switches up = "Set from KISS" = software control.
    /// </summary>
    public byte? DipSwitches { get; init; }

    /// <summary>
    /// <see cref="DipSwitches"/> rendered as four binary digits (e.g.
    /// <c>"1111"</c>), or <c>null</c> when the register was missing.
    /// </summary>
    public string? DipSwitchesBinary =>
        DipSwitches is { } d ? Convert.ToString(d, 2).PadLeft(4, '0') : null;

    /// <summary>
    /// True when the DIP switches read <c>1111</c> — the TNC's mode is
    /// under software (KISS SETHW) control rather than pinned by the DIPs.
    /// <c>null</c> when register 04 was missing.
    /// </summary>
    public bool? IsSoftwareControlMode => DipSwitches is { } d ? d == 0x0F : null;

    /// <summary>
    /// Register 06 — the configured-mode identifier byte, the same value the
    /// labelled diagnostic packs into <c>BrdSwchMod</c>'s low byte. Resolve
    /// through <see cref="NinoTncCatalog.TryGetByFirmwareByte"/> (or read
    /// <see cref="RunningMode"/>).
    /// </summary>
    public byte? FirmwareModeByte { get; init; }

    /// <summary>
    /// The mode the TNC is currently running, resolved from
    /// <see cref="FirmwareModeByte"/> through the catalog. <c>null</c> if
    /// unknown to the catalog (firmware likely newer than ours).
    /// </summary>
    public NinoTncMode? RunningMode { get; init; }

    /// <summary>Register 07 — AX.25 packets received since boot.</summary>
    public long? Ax25RxPackets { get; init; }

    /// <summary>Register 08 — IL2P packets received and corrected (correctable RX) since boot.</summary>
    public long? Il2pRxCorrectable { get; init; }

    /// <summary>Register 09 — IL2P packets received with uncorrectable errors since boot.</summary>
    public long? Il2pRxUncorrectable { get; init; }

    /// <summary>Register 0A — packets transmitted since boot.</summary>
    public long? TxPackets { get; init; }

    /// <summary>
    /// Register 0B — preamble word count. Preamble seconds = count × 16 ÷
    /// bit rate (each word is 16 bits), so a delta across a known number of
    /// transmissions measures the effective TXDELAY — see
    /// <see cref="NinoTncStatusDelta.PreambleSeconds"/>.
    /// </summary>
    public long? PreambleWordCount { get; init; }

    /// <summary>Register 0C — firmware main-loop cycles since boot.</summary>
    public long? LoopCycles { get; init; }

    /// <summary>Register 0D — cumulative PTT-asserted time in milliseconds.</summary>
    public long? PttOnMs { get; init; }

    /// <summary>Register 0E — cumulative DCD-asserted time in milliseconds.</summary>
    public long? DcdOnMs { get; init; }

    /// <summary>Register 0F — bytes received since boot.</summary>
    public long? RxBytes { get; init; }

    /// <summary>Register 10 — bytes transmitted since boot.</summary>
    public long? TxBytes { get; init; }

    /// <summary>Register 11 — IL2P bytes repaired by FEC since boot.</summary>
    public long? Il2pFecCorrectedBytes { get; init; }

    /// <summary>
    /// Dropped ADC samples since boot. No numeric register is known to
    /// carry this (registers 00–11 observed), so it is populated only when
    /// the snapshot was mapped from the labelled diagnostic's
    /// <c>LostADCSmp</c> field via <see cref="FromDiagnostic"/> — which is
    /// what GETALL answers with on firmware 3.41 and 3.44. A rising delta
    /// while receiving means the RX audio is clipping the TNC's ADC —
    /// gross over-deviation at the transmitting end.
    /// </summary>
    public long? LostAdcSamples { get; init; }

    /// <summary>
    /// Every register the frame carried, raw and unmapped (register index →
    /// verbatim value bytes). Registers this type has no property for
    /// (e.g. additions in newer firmware) are still present here.
    /// </summary>
    public IReadOnlyDictionary<byte, byte[]> RawRegisters { get; init; } =
        new Dictionary<byte, byte[]>();

    /// <summary>
    /// Try to parse a numeric status report out of a decoded KISS data frame.
    /// </summary>
    public static bool TryParse(KissFrame frame, out NinoTncStatusFrame? parsed)
    {
        parsed = null;
        if (frame.Command != KissCommand.Data)
        {
            return false;
        }
        return TryParse(frame.Payload, out parsed);
    }

    /// <summary>
    /// Try to parse a numeric status report out of raw KISS-frame payload
    /// bytes. Scans for the <c>=00:</c> marker rather than assuming an
    /// AX.25-shaped prefix (the bytes before it are a fake address header).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out NinoTncStatusFrame? parsed)
    {
        parsed = null;
        int start = IndexOfFirstMarker(payload);
        if (start < 0)
        {
            return false;
        }

        var registers = new Dictionary<byte, byte[]>();
        int i = start;
        while (IsMarkerAt(payload, i))
        {
            byte register = (byte)((HexValue(payload[i + 1]) << 4) | HexValue(payload[i + 2]));
            i += 4;

            byte[] value;
            if (register == SerialNumberRegister)
            {
                // Register 01 is eight RAW bytes (which may include '=', ':',
                // or anything else) — take them positionally.
                int take = Math.Min(8, payload.Length - i);
                value = payload.Slice(i, take).ToArray();
                i += take;
            }
            else
            {
                int end = i;
                while (end < payload.Length && !IsMarkerAt(payload, end))
                {
                    end++;
                }
                value = payload[i..end].ToArray();
                i = end;
            }
            registers[register] = value;
        }

        if (registers.Count == 0)
        {
            return false;
        }

        string? fwRaw = TryAsciiRegister(registers, FirmwareVersionRegister);
        byte? modeByte = TryHexRegister(registers, 0x06) is { } mode ? (byte)(mode & 0xFF) : null;
        parsed = new NinoTncStatusFrame
        {
            FirmwareVersionRaw = fwRaw,
            FirmwareVersion = Firmware.NinoTncFirmwareVersion.TryParse(fwRaw, out var fwVersion) ? fwVersion : null,
            SerialNumber = NormaliseSerial(registers.GetValueOrDefault(SerialNumberRegister)),
            UptimeMs = TryHexRegister(registers, 0x02),
            BoardId = TryHexRegister(registers, 0x03),
            DipSwitches = TryHexRegister(registers, 0x04) is { } dip ? (byte)(dip & 0x0F) : null,
            FirmwareModeByte = modeByte,
            RunningMode = modeByte is { } m ? NinoTncCatalog.TryGetByFirmwareByte(m) : null,
            Ax25RxPackets = TryHexRegister(registers, 0x07),
            Il2pRxCorrectable = TryHexRegister(registers, 0x08),
            Il2pRxUncorrectable = TryHexRegister(registers, 0x09),
            TxPackets = TryHexRegister(registers, 0x0A),
            PreambleWordCount = TryHexRegister(registers, 0x0B),
            LoopCycles = TryHexRegister(registers, 0x0C),
            PttOnMs = TryHexRegister(registers, 0x0D),
            DcdOnMs = TryHexRegister(registers, 0x0E),
            RxBytes = TryHexRegister(registers, 0x0F),
            TxBytes = TryHexRegister(registers, 0x10),
            Il2pFecCorrectedBytes = TryHexRegister(registers, 0x11),
            RawRegisters = registers,
        };
        return true;
    }

    /// <summary>
    /// Map a labelled <c>=FirmwareVr:</c> diagnostic
    /// (<see cref="NinoTncTxTestFrame"/>) into this numeric-report shape.
    /// Firmware 3.41 <em>and</em> 3.44 answer GETALL with the labelled text
    /// (bench-verified 2026-07-02), which carries a subset of the registers —
    /// the fields with no labelled counterpart (PTT-on, DCD-on, RX/TX bytes,
    /// FEC-corrected bytes) stay <c>null</c>; <see cref="LostAdcSamples"/>
    /// conversely exists <em>only</em> via this labelled path.
    /// </summary>
    public static NinoTncStatusFrame FromDiagnostic(NinoTncTxTestFrame diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new NinoTncStatusFrame
        {
            FirmwareVersionRaw = diagnostic.FirmwareVersionRaw,
            FirmwareVersion = diagnostic.FirmwareVersion,
            SerialNumber = diagnostic.SerialNumber,
            UptimeMs = diagnostic.UptimeMs,
            BoardId = diagnostic.BoardRevision,
            DipSwitches = diagnostic.DipSwitchPosition,
            FirmwareModeByte = diagnostic.FirmwareModeByte,
            RunningMode = diagnostic.RunningMode,
            Ax25RxPackets = diagnostic.Ax25RxPackets,
            Il2pRxCorrectable = diagnostic.Il2pRxPackets,
            Il2pRxUncorrectable = diagnostic.Il2pRxUncorrectable,
            TxPackets = diagnostic.TxPacketCount,
            PreambleWordCount = diagnostic.PreambleCount,
            LoopCycles = diagnostic.LoopCycles,
            LostAdcSamples = diagnostic.LostAdcSamples,
        };
    }

    private static long? TryHexRegister(Dictionary<byte, byte[]> registers, byte register)
    {
        if (!registers.TryGetValue(register, out var raw) || raw.Length == 0)
        {
            return null;
        }
        string text = Encoding.ASCII.GetString(raw);
        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? TryAsciiRegister(Dictionary<byte, byte[]> registers, byte register)
    {
        if (!registers.TryGetValue(register, out var raw) || raw.Length == 0)
        {
            return null;
        }
        return Encoding.ASCII.GetString(raw);
    }

    private static string? NormaliseSerial(byte[]? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var cleaned = Encoding.ASCII.GetString(raw)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static int IndexOfFirstMarker(ReadOnlySpan<byte> payload)
    {
        // The report always starts at register 00; requiring the "=00:"
        // marker keeps random '=' bytes in ordinary traffic from matching.
        for (int i = 0; i + 4 <= payload.Length; i++)
        {
            if (payload[i] == (byte)'=' && payload[i + 1] == (byte)'0' &&
                payload[i + 2] == (byte)'0' && payload[i + 3] == (byte)':')
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsMarkerAt(ReadOnlySpan<byte> payload, int index) =>
        index >= 0 &&
        index + 4 <= payload.Length &&
        payload[index] == (byte)'=' &&
        IsHex(payload[index + 1]) &&
        IsHex(payload[index + 2]) &&
        payload[index + 3] == (byte)':';

    private static bool IsHex(byte b) =>
        b is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'A' and <= (byte)'F'
            or >= (byte)'a' and <= (byte)'f';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => b - (byte)'a' + 10,
    };
}
