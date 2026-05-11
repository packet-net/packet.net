namespace Packet.Kiss.NinoTnc;

/// <summary>
/// One NinoTNC operating mode — the (mode number, human name, raw bit rate)
/// triple. Mode numbers correspond to the DIP-switch position on the TNC's
/// front panel (or the value set via <see cref="KissCommand.SetHardware"/>
/// when DIP=15 "Set from KISS").
/// </summary>
/// <param name="Mode">DIP-switch position, 0–15.</param>
/// <param name="Name">Human-readable description, e.g. "1200 AFSK AX.25".</param>
/// <param name="BitRateHz">
/// Raw data rate in bits per second (symbol rate × bits per symbol).
/// <c>0</c> for the special <em>mode 15</em> entry, which is variable.
/// </param>
public readonly record struct NinoTncMode(byte Mode, string Name, int BitRateHz)
{
    /// <summary>
    /// Compute the time it takes to transmit a frame of <paramref name="frameBytes"/>
    /// bytes at this mode's raw bit rate, in milliseconds. Returns
    /// <see cref="double.PositiveInfinity"/> for variable-rate modes.
    /// </summary>
    public double TransmissionMs(int frameBytes)
    {
        if (BitRateHz <= 0)
        {
            return double.PositiveInfinity;
        }
        return frameBytes * 8.0 / BitRateHz * 1000.0;
    }
}
