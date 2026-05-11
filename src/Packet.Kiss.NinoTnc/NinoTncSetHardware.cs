namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Build NinoTNC-specific <see cref="KissCommand.SetHardware"/> payloads.
/// </summary>
public static class NinoTncSetHardware
{
    /// <summary>
    /// Add this to a mode number to instruct the NinoTNC to honour the mode
    /// for the current power cycle only, leaving the flash-stored mode
    /// unchanged. Ports of long-running operators avoid burning flash
    /// write cycles by passing <c>persistToFlash: false</c>.
    /// </summary>
    public const byte NonPersistOffset = 16;

    /// <summary>
    /// Maximum valid mode number (DIP position 0–15).
    /// </summary>
    public const byte MaxMode = 15;

    /// <summary>
    /// Compute the single-byte SETHW payload for a given mode and persist
    /// preference.
    /// </summary>
    /// <param name="mode">DIP-switch-equivalent mode 0–15.</param>
    /// <param name="persistToFlash">
    /// If <c>true</c>, the TNC writes the new mode to flash so it survives a
    /// reboot. If <c>false</c>, the change is RAM-only — this is the
    /// commonly-preferred default in tooling because flash has limited write
    /// cycles.
    /// </param>
    public static byte BuildPayloadByte(byte mode, bool persistToFlash)
    {
        if (mode > MaxMode)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, $"NinoTNC mode must be 0–{MaxMode}");
        }
        return persistToFlash ? mode : (byte)(mode + NonPersistOffset);
    }

    /// <summary>
    /// Build a fully-encoded KISS SETHW frame for the given mode.
    /// </summary>
    public static byte[] BuildKissFrame(byte mode, bool persistToFlash, byte port = 0)
    {
        byte payload = BuildPayloadByte(mode, persistToFlash);
        return KissEncoder.Encode(port, KissCommand.SetHardware, new[] { payload });
    }
}
