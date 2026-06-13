using System.ComponentModel;

namespace Packet.Mcp.Decoding;

/// <summary>
/// The human-readable decode of a single AX.25 frame — what <c>decode_frame</c>
/// returns. A pure projection of <c>Packet.Ax25.Ax25Frame</c> (plus the KISS
/// wrapper when present); no node state involved.
/// </summary>
public sealed record DecodedFrame(
    [property: Description("Framing the input was read as: raw (bare AX.25) or kiss.")] string Framing,
    [property: Description("KISS multi-drop port, when the input was a KISS frame; else null.")] int? KissPort,
    [property: Description("Source callsign with SSID.")] string Source,
    [property: Description("Destination callsign with SSID.")] string Destination,
    [property: Description("Digipeater path in order; the * marks a repeated hop (H-bit set).")] IReadOnlyList<string> Path,
    [property: Description("command | response | legacy (v1/unspecified C-bit pairing).")] string CommandResponse,
    [property: Description("Frame class: I, S, or U.")] string FrameClass,
    [property: Description("Frame type: I, RR, RNR, REJ, SREJ, SABM, SABME, DISC, UA, DM, FRMR, UI, XID, TEST.")] string FrameType,
    [property: Description("Whether the poll/final bit is set.")] bool PollFinal,
    [property: Description("Control modulo this was decoded under: 8 or 128.")] int Modulo,
    [property: Description("N(R) receive sequence number, when the type carries one.")] int? Nr,
    [property: Description("N(S) send sequence number, for I frames.")] int? Ns,
    [property: Description("PID byte (e.g. 0xF0), when present.")] byte? Pid,
    [property: Description("Human name for the PID, when recognised.")] string? PidName,
    [property: Description("Information field length in bytes.")] int InfoLength,
    [property: Description("Information field as hex.")] string InfoHex,
    [property: Description("Information field rendered as text (non-printables shown as '.').")] string InfoText);
