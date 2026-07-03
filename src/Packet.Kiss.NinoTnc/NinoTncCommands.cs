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
    /// True when <paramref name="frame"/> is a firmware query reply — i.e.
    /// its raw KISS command byte is <see cref="ReplyCommandByte"/> (0xE0).
    /// </summary>
    public static bool IsReply(KissFrame frame) =>
        (byte)(((frame.Port & 0x0F) << 4) | ((byte)frame.Command & 0x0F)) == ReplyCommandByte;
}
