using System.Collections.Frozen;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The NinoTNC mode catalog. Modes 0–14 are concrete operating modes
/// selected by the front-panel DIP switches; mode 15 is the "Set from KISS"
/// escape that uses <see cref="KissCommand.SetHardware"/> to choose the
/// effective mode at runtime.
/// </summary>
/// <remarks>
/// Ported from M0LTE/kissproxy@web-interface, <c>KissFrameBuilder.cs</c> lines
/// 45–112. The DIP-position → human-name table and the firmware-byte →
/// human-name table are both kept verbatim because:
/// <list type="bullet">
///   <item>Firmware-reported mode bytes (in TX-Test frames) are a small,
///         fixed set per firmware version and may not equal the DIP position
///         (the firmware encodes the underlying modem configuration, not the
///         user-facing switch label). We need the reverse lookup to identify
///         the operating mode from a TX-Test frame.</item>
///   <item>The catalog is firmware-version-specific (currently v3.44). Newer
///         firmwares may extend it; bump when needed.</item>
/// </list>
/// </remarks>
public static class NinoTncCatalog
{
    /// <summary>
    /// All 16 NinoTNC modes keyed by DIP-switch position.
    /// </summary>
    public static readonly FrozenDictionary<byte, NinoTncMode> ByMode = new[]
    {
        new NinoTncMode(0,  "9600 GFSK AX.25",      9600),
        new NinoTncMode(1,  "19200 4FSK",          19200),
        new NinoTncMode(2,  "9600 GFSK IL2P+CRC",   9600),
        new NinoTncMode(3,  "9600 4FSK",            9600),
        new NinoTncMode(4,  "4800 GFSK IL2P+CRC",   4800),
        new NinoTncMode(5,  "3600 QPSK IL2P+CRC",   3600),
        new NinoTncMode(6,  "1200 AFSK AX.25",      1200),
        new NinoTncMode(7,  "1200 AFSK IL2P+CRC",   1200),
        new NinoTncMode(8,  "300 BPSK IL2P+CRC",     300),
        new NinoTncMode(9,  "600 QPSK IL2P+CRC",     600),
        new NinoTncMode(10, "1200 BPSK IL2P+CRC",   1200),
        new NinoTncMode(11, "2400 QPSK IL2P+CRC",   2400),
        new NinoTncMode(12, "300 AFSK AX.25",        300),
        new NinoTncMode(13, "300 AFSKPLL IL2P",      300),
        new NinoTncMode(14, "300 AFSKPLL IL2P+CRC",  300),
        new NinoTncMode(15, "Set from KISS",           0),
    }.ToFrozenDictionary(m => m.Mode);

    /// <summary>
    /// Firmware-reported mode bytes (the lower byte of the BrdSwchMod field
    /// in a TX-Test frame) → DIP-switch-position mode. Used to decode the
    /// "actual operating mode" when DIP=15 ("Set from KISS").
    /// </summary>
    /// <remarks>Firmware-version-specific. Locked to NinoTNC firmware v3.44.</remarks>
    public static readonly FrozenDictionary<byte, byte> FirmwareByteToMode = new Dictionary<byte, byte>
    {
        { 0x00, 0  },
        { 0x41, 1  },
        { 0xB0, 2  },
        { 0x40, 3  },
        { 0xA3, 4  },
        { 0xF1, 5  },
        { 0x02, 6  },
        { 0x93, 7  },
        { 0x91, 8  },
        { 0x92, 9  },
        { 0xA0, 10 },
        { 0xA2, 11 },
        { 0x31, 12 },
        { 0x22, 13 },
        { 0x23, 14 },
        { 0xF3, 15 },
    }.ToFrozenDictionary();

    /// <summary>
    /// Look up a mode by its DIP-switch position. Returns <c>null</c> for
    /// out-of-range mode numbers.
    /// </summary>
    public static NinoTncMode? TryGetByMode(byte mode) =>
        ByMode.TryGetValue(mode, out var m) ? m : null;

    /// <summary>
    /// Look up the mode the firmware is currently *running* given the byte
    /// it reports in a TX-Test frame's BrdSwchMod field. Returns <c>null</c>
    /// for unrecognised firmware values (a clue that the firmware has been
    /// updated and our table needs refreshing).
    /// </summary>
    public static NinoTncMode? TryGetByFirmwareByte(byte firmwareByte)
    {
        if (!FirmwareByteToMode.TryGetValue(firmwareByte, out var mode))
        {
            return null;
        }
        return ByMode[mode];
    }
}
