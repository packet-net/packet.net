namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// A firmware-flash progress snapshot: how many Intel-HEX lines the
/// bootloader has accepted out of the image total. Reported once per
/// accepted line — a full-size image is ~16–17 k lines over 2–4 minutes,
/// so consumers wanting a lighter cadence should throttle on
/// <see cref="Percent"/> changes.
/// </summary>
/// <param name="LinesWritten">Lines the bootloader has accepted so far.</param>
/// <param name="TotalLines">Total lines in the image.</param>
public readonly record struct NinoTncFlashProgress(int LinesWritten, int TotalLines)
{
    /// <summary>Completion as a fraction 0..1.</summary>
    public double Fraction => TotalLines <= 0 ? 0 : (double)LinesWritten / TotalLines;

    /// <summary>Completion as an integer percentage 0..100.</summary>
    public int Percent => (int)(Fraction * 100);

    /// <inheritdoc/>
    public override string ToString() => $"{LinesWritten}/{TotalLines} ({Percent}%)";
}
