namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Build NinoTNC-specific control-command KISS frames — the firmware's
/// query/control channel beyond standard KISS. These command codes are
/// NinoTNC firmware extensions (they are not part of the KISS spec), so
/// they live here rather than in <see cref="KissCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replies do not come back on the command's own code: the firmware
/// answers on the KISS command byte <see cref="ReplyCommandByte"/>
/// (<c>0xE0</c> — which the generic decoder surfaces as port 14 /
/// command <see cref="KissCommand.Data"/>; use <see cref="IsReply"/>
/// to spot them), or — for the periodic status report — as a fake UI
/// frame on a plain KISS Data frame (see <see cref="NinoTncStatusFrame"/>).
/// </para>
/// <para>
/// Bench-verified against firmware 3.41 and 3.44 (2026-07-02, 2× NinoTNC
/// on the Tait rig): GETVER answers the bare ASCII version (<c>"3.41"</c> /
/// <c>"3.44"</c>); GETALL answers the labelled <c>=FirmwareVr:</c>
/// diagnostic text (<see cref="NinoTncTxTestFrame"/>) on 0xE0 on <em>both</em>
/// firmwares — the numeric <c>=II:</c> register report only ever arrives as
/// the periodic status beacon; GETRSSI answers <c>"RSSI:-32.86"</c>-style
/// ASCII (see <see cref="NinoTncRssiReading"/>) on 3.41 but was
/// <b>removed in 3.44</b> (no reply at all — it was an undocumented 3.41
/// feature).
/// </para>
/// </remarks>
public static class NinoTncCommands
{
    /// <summary>KISS command code for GETVER — request the firmware version string.</summary>
    public const byte GetVersionCommand = 0x08;

    /// <summary>
    /// KISS command code shared by the extended sub-commands: STOPTX
    /// (payload <see cref="StopTxSubcommand"/>), SETBCNINT (payload
    /// <see cref="SetBeaconIntervalSubcommand"/> + minutes), and GETRSSI
    /// (payload <see cref="GetRssiSubcommand"/>).
    /// </summary>
    public const byte ExtendedCommand = 0x09;

    /// <summary>KISS command code for GETALL — request a full diagnostic-register report.</summary>
    public const byte GetAllCommand = 0x0B;

    /// <summary>
    /// KISS command code for SETSERNO — write the TNC's 8-byte KAUP8R
    /// identity register (the value GETALL reports as register 01). The
    /// payload is exactly 8 ASCII characters; all-zeroes clears the register
    /// (upstream tnc-tools calls that CLRSERNO, and prescribes clearing
    /// before setting). Fire-and-forget — the firmware sends no
    /// acknowledgement; read it back with <see cref="GetSerialNumberCommand"/>.
    /// </summary>
    public const byte SetSerialNumberCommand = 0x0A;

    /// <summary>
    /// KISS command code for GETSERNO — query the TNC's 8-byte KAUP8R
    /// identity register. The reply is exactly 8 raw bytes on
    /// <see cref="ReplyCommandByte"/> (all-zero = register unset).
    /// Bench-verified on firmware 3.41 (2026-07-03).
    /// </summary>
    public const byte GetSerialNumberCommand = 0x0E;

    /// <summary>The KAUP8R identity register length, in bytes.</summary>
    public const int SerialNumberLength = 8;

    /// <summary>
    /// KISS command code for BOOTLOADER — switch the TNC from KISS mode
    /// into the dsPIC bootloader for a firmware flash. The payload must be
    /// the single magic byte <see cref="BootloaderEntryMagic"/>; the
    /// bootloader answers with a raw ASCII <c>'K'</c> (not a KISS frame —
    /// from this moment the port speaks the bare bootloader protocol, see
    /// <see cref="Firmware.BootloaderNinoTncFirmwareFlasher"/>).
    /// </summary>
    public const byte BootloaderCommand = 0x0D;

    /// <summary>
    /// The magic payload byte of <see cref="BootloaderCommand"/> — a
    /// deliberate speed bump so a stray 0x0D command can't reboot the modem
    /// into the bootloader.
    /// </summary>
    public const byte BootloaderEntryMagic = 0x37;

    /// <summary>
    /// The raw KISS command byte the firmware uses for direct query replies
    /// (GETVER / GETALL / GETRSSI). Decodes as port 14 + command 0x0
    /// through a standard multi-drop KISS decoder.
    /// </summary>
    public const byte ReplyCommandByte = 0xE0;

    /// <summary><see cref="ExtendedCommand"/> payload byte for STOPTX — abort any transmission in progress.</summary>
    public const byte StopTxSubcommand = 0x00;

    /// <summary>
    /// <see cref="ExtendedCommand"/> payload prefix for SETBCNINT — set the
    /// interval of the periodic status report (<see cref="NinoTncStatusFrame"/>),
    /// in minutes. Firmware 3.41 defaults to one report per minute.
    /// </summary>
    public const byte SetBeaconIntervalSubcommand = 0xF0;

    /// <summary><see cref="ExtendedCommand"/> payload byte for GETRSSI — request an RX-audio
    /// level reading. Firmware 3.41 only; removed in 3.44 (the query gets no reply).</summary>
    public const byte GetRssiSubcommand = 0xA7;

    /// <summary>GETALL request payload (a single 0x00 byte).</summary>
    public static byte[] BuildGetAllPayload() => [0x00];

    /// <summary>GETVER request payload (a single 0x00 byte).</summary>
    public static byte[] BuildGetVersionPayload() => [0x00];

    /// <summary>STOPTX request payload.</summary>
    public static byte[] BuildStopTxPayload() => [StopTxSubcommand];

    /// <summary>SETBCNINT request payload: 0xF0 followed by the interval in minutes.</summary>
    public static byte[] BuildSetBeaconIntervalPayload(byte minutes) => [SetBeaconIntervalSubcommand, minutes];

    /// <summary>GETRSSI request payload.</summary>
    public static byte[] BuildGetRssiPayload() => [GetRssiSubcommand];

    /// <summary>BOOTLOADER entry payload (the single magic byte <see cref="BootloaderEntryMagic"/>).</summary>
    public static byte[] BuildBootloaderEntryPayload() => [BootloaderEntryMagic];

    /// <summary>SETSERNO payload: exactly 8 ASCII characters for the KAUP8R register.</summary>
    /// <exception cref="ArgumentException"><paramref name="serialNumber"/> is not exactly
    /// 8 characters, or contains non-ASCII characters.</exception>
    public static byte[] BuildSetSerialNumberPayload(string serialNumber)
    {
        ArgumentNullException.ThrowIfNull(serialNumber);
        if (serialNumber.Length != SerialNumberLength)
        {
            throw new ArgumentException(
                $"the KAUP8R serial number is exactly {SerialNumberLength} characters", nameof(serialNumber));
        }
        var payload = new byte[SerialNumberLength];
        for (int i = 0; i < payload.Length; i++)
        {
            char c = serialNumber[i];
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException(
                    $"the KAUP8R serial number must be printable ASCII (offset {i} is U+{(int)c:X4})",
                    nameof(serialNumber));
            }
            payload[i] = (byte)c;
        }
        return payload;
    }

    /// <summary>CLRSERNO payload: 8 zero bytes — clears the KAUP8R register.</summary>
    public static byte[] BuildClearSerialNumberPayload() => new byte[SerialNumberLength];

    /// <summary>GETSERNO request payload (a single 0x00 byte).</summary>
    public static byte[] BuildGetSerialNumberPayload() => [0x00];

    /// <summary>Fully-encoded GETALL KISS frame, ready for the wire.</summary>
    public static byte[] BuildGetAllKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)GetAllCommand, BuildGetAllPayload());

    /// <summary>Fully-encoded GETVER KISS frame, ready for the wire.</summary>
    public static byte[] BuildGetVersionKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)GetVersionCommand, BuildGetVersionPayload());

    /// <summary>Fully-encoded STOPTX KISS frame, ready for the wire.</summary>
    public static byte[] BuildStopTxKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)ExtendedCommand, BuildStopTxPayload());

    /// <summary>Fully-encoded SETBCNINT KISS frame, ready for the wire.</summary>
    public static byte[] BuildSetBeaconIntervalKissFrame(byte minutes, byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)ExtendedCommand, BuildSetBeaconIntervalPayload(minutes));

    /// <summary>Fully-encoded GETRSSI KISS frame, ready for the wire.</summary>
    public static byte[] BuildGetRssiKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)ExtendedCommand, BuildGetRssiPayload());

    /// <summary>
    /// Fully-encoded BOOTLOADER-entry KISS frame (<c>C0 0D 37 C0</c>), ready
    /// for the wire. <b>Sending this reboots the modem into the dsPIC
    /// bootloader</b> — KISS stops answering until a firmware image is
    /// transferred (or the bootloader is told <c>'R'</c> to return to the
    /// application). Use <see cref="Firmware.BootloaderNinoTncFirmwareFlasher"/>
    /// rather than sending it by hand.
    /// </summary>
    public static byte[] BuildBootloaderEntryKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)BootloaderCommand, BuildBootloaderEntryPayload());

    /// <summary>
    /// Fully-encoded payload-less GETALL KISS frame (<c>C0 0B C0</c>) — the
    /// exact bytes upstream <c>flashtnc.py</c> uses to provoke output while
    /// flushing the serial path before a flash. The firmware accepts GETALL
    /// with or without the 0x00 payload byte; the flasher sends this form to
    /// stay byte-identical with the protocol that has been validated on real
    /// hardware. Normal callers should prefer <see cref="BuildGetAllKissFrame"/>.
    /// </summary>
    public static byte[] BuildBareGetAllKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)GetAllCommand, ReadOnlySpan<byte>.Empty);

    /// <summary>Fully-encoded SETSERNO KISS frame, ready for the wire. <b>Writes the TNC's
    /// KAUP8R identity register.</b></summary>
    public static byte[] BuildSetSerialNumberKissFrame(string serialNumber, byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)SetSerialNumberCommand, BuildSetSerialNumberPayload(serialNumber));

    /// <summary>Fully-encoded CLRSERNO KISS frame, ready for the wire. <b>Clears the TNC's
    /// KAUP8R identity register.</b></summary>
    public static byte[] BuildClearSerialNumberKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)SetSerialNumberCommand, BuildClearSerialNumberPayload());

    /// <summary>Fully-encoded GETSERNO KISS frame, ready for the wire.</summary>
    public static byte[] BuildGetSerialNumberKissFrame(byte port = 0) =>
        KissEncoder.Encode(port, (KissCommand)GetSerialNumberCommand, BuildGetSerialNumberPayload());

    /// <summary>
    /// True when <paramref name="frame"/> is a firmware query reply — i.e.
    /// its raw KISS command byte is <see cref="ReplyCommandByte"/> (0xE0).
    /// </summary>
    public static bool IsReply(KissFrame frame) =>
        (byte)(((frame.Port & 0x0F) << 4) | ((byte)frame.Command & 0x0F)) == ReplyCommandByte;
}
