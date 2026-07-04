using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Pure matching of a configured CCDI serial number to a discovered radio — the stable-identity
/// resolution behind a <c>serial:</c>-bound port. Factored out of <see cref="RadioControlFactory"/>
/// so the "which device is this radio on right now?" decision is unit-testable without opening a
/// serial port.
/// </summary>
public static class RadioSerialResolver
{
    /// <summary>
    /// The discovered radio whose CCDI serial number matches <paramref name="serial"/>, or
    /// <c>null</c> when none does. Comparison is case-insensitive and trims surrounding whitespace
    /// (a serial pasted from a label often carries stray spaces); a blank target never matches.
    /// This is the resolution device-path binding can't offer: two CP2102 CCDI dongles share a USB
    /// serial (so <c>/dev/serial/by-id</c> collides), but their CCDI serials differ, so this picks
    /// the right one even after <c>/dev/ttyUSB*</c> renumbering.
    /// </summary>
    public static TaitDiscoveredRadio? Match(IReadOnlyList<TaitDiscoveredRadio> found, string serial)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }
        var target = serial.Trim();
        foreach (var radio in found)
        {
            if (string.Equals(radio.Identity.SerialNumber?.Trim(), target, StringComparison.OrdinalIgnoreCase))
            {
                return radio;
            }
        }
        return null;
    }
}
