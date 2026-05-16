namespace Packet.Kiss;

/// <summary>
/// One decoded KISS frame: a port number, a command, and the raw payload.
/// </summary>
/// <param name="Port">Multi-drop port number, 0–15. 0 for a single-port TNC.</param>
/// <param name="Command">KISS command code (low nibble of the command byte).</param>
/// <param name="Payload">
/// Raw bytes between the command byte and the closing FEND. For
/// <see cref="KissCommand.Data"/> this is the AX.25 frame minus the FCS
/// (the TNC strips the FCS on RX and inserts one on TX). For parameter
/// commands (TXDELAY etc.) it is the parameter value byte(s).
/// </param>
public readonly record struct KissFrame(byte Port, KissCommand Command, byte[] Payload);
