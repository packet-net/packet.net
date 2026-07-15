using System.Collections.Frozen;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The NinoTNC mode catalog. Modes 0–14 are concrete operating modes
/// selected by the front-panel DIP switches; mode 15 is the "Set from KISS"
/// escape that uses <see cref="KissCommand.SetHardware"/> to choose the
/// effective mode at runtime.
/// </summary>
/// <remarks>
/// Originally ported from packet-net/kissproxy@web-interface,
/// <c>KissFrameBuilder.cs</c> lines 45–112; the DIP-position names are now
/// reconciled against Nino's own v44 mode table (see <see cref="ByMode"/>).
/// The firmware-byte → mode table is kept verbatim because:
/// <list type="bullet">
///   <item>Firmware-reported mode bytes (in TX-Test frames) are a small,
///         fixed set per firmware version and may not equal the DIP position
///         (the firmware encodes the underlying modem configuration, not the
///         user-facing switch label). We need the reverse lookup to identify
///         the operating mode from a TX-Test frame.</item>
///   <item>The catalog is firmware-version-specific (base table: v3.44;
///         bench-verified 3.41 divergences are carried as extra alias rows —
///         see <see cref="FirmwareByteToMode"/>). Newer firmwares may extend
///         it; bump when needed.</item>
/// </list>
/// </remarks>
public static class NinoTncCatalog
{
    /// <summary>
    /// All 16 NinoTNC modes keyed by DIP-switch position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names reconciled 2026-07-15 against Nino's own v44 mode table —
    /// <see href="https://github.com/ninocarrillo/flashtnc/blob/master/v44-op-modes.png">v44-op-modes.png</see>
    /// plus the "MODE SWITCH MAPPING v3/4.43" block in
    /// <see href="https://github.com/ninocarrillo/flashtnc/blob/master/release-notes.txt">release-notes.txt</see>,
    /// which is the upstream source for both the names and the RF grouping.
    /// Two corrections landed from it (the previous names came from
    /// kissproxy, itself predating v42):
    /// </para>
    /// <list type="bullet">
    ///   <item>Modes 1/3 are <em>C4FSK</em> — coherent 4-level FSK, added in
    ///         firmware 3/4.42 — not the bare "4FSK" kissproxy called them.</item>
    ///   <item>Modes 13/14 are plain "300 AFSK"; the "AFSKPLL" spelling is
    ///         retired upstream (3/4.42 gave every 300 AFSK mode coherent
    ///         demodulation, so the PLL variant is no longer a distinct
    ///         thing to name).</item>
    /// </list>
    /// <para>
    /// The IL2P+CRC spelling stays "IL2P+CRC" rather than upstream's "IL2Pc":
    /// it is what the rest of this codebase filters on, and the two names are
    /// the same protocol.
    /// </para>
    /// </remarks>
    public static readonly FrozenDictionary<byte, NinoTncMode> ByMode = new[]
    {
        // GFSK / C4FSK modes: need an FM radio with a '9600' data port or a
        // discriminator/varactor connection.
        new NinoTncMode(0,  "9600 GFSK AX.25",       9600),
        new NinoTncMode(1,  "19200 C4FSK IL2P+CRC", 19200),
        new NinoTncMode(2,  "9600 GFSK IL2P+CRC",    9600),
        new NinoTncMode(3,  "9600 C4FSK IL2P+CRC",   9600),
        new NinoTncMode(4,  "4800 GFSK IL2P+CRC",    4800),

        // FM AFSK modes: fine through a speaker/mic connection.
        new NinoTncMode(5,  "3600 QPSK IL2P+CRC",    3600),
        new NinoTncMode(6,  "1200 AFSK AX.25",       1200),
        new NinoTncMode(7,  "1200 AFSK IL2P+CRC",    1200),

        // Shaped-PSK modes (SSB or FM): phase modulation of a 1500 Hz tone.
        new NinoTncMode(8,  "300 BPSK IL2P+CRC",      300),
        new NinoTncMode(9,  "600 QPSK IL2P+CRC",      600),
        new NinoTncMode(10, "1200 BPSK IL2P+CRC",    1200),
        new NinoTncMode(11, "2400 QPSK IL2P+CRC",    2400),

        // SSB AFSK modes: legacy HF packet, 1600/1800 Hz tone FSK.
        new NinoTncMode(12, "300 AFSK AX.25",         300),
        new NinoTncMode(13, "300 AFSK IL2P",          300),
        new NinoTncMode(14, "300 AFSK IL2P+CRC",      300),

        new NinoTncMode(15, "Set from KISS",            0),
    }.ToFrozenDictionary(m => m.Mode);

    /// <summary>
    /// Firmware-reported mode bytes (the lower byte of the BrdSwchMod field
    /// in a TX-Test frame) → DIP-switch-position mode. Used to decode the
    /// "actual operating mode" when DIP=15 ("Set from KISS").
    /// </summary>
    /// <remarks>Firmware-version-specific. The base table is locked to
    /// NinoTNC firmware v3.44; where an older firmware is bench-verified to
    /// report a <em>different</em> byte for a mode, that byte is added as an
    /// extra row (the reverse lookup tolerates aliases — several bytes may
    /// resolve to one mode).</remarks>
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
        // Firmware 3.41 reports mode 14 (300 AFSK IL2P+CRC) as 0x90 where
        // 3.44 reports 0x23 — bench evidence: the 2026-07-03 wide-il2pc
        // mode-survey runs, where the GETALL verify read "unrecognised
        // firmware byte 0x90" while the 300 AFSK traffic decoding proved
        // the mode was engaged.
        { 0x90, 14 },
        { 0xF3, 15 },
    }.ToFrozenDictionary();

    /// <summary>
    /// Modes whose published occupied bandwidth needs a wide (25 kHz) channel:
    /// 0 (9600 GFSK AX.25), 1 (19200 C4FSK IL2P+CRC) and 2 (9600 GFSK
    /// IL2P+CRC) — exactly the modes Nino's v3/4.43 mode-switch mapping rates
    /// at 20 kHz OBW, the rest being 10 kHz (modes 3, 4) or narrower. Note
    /// mode 3 (9600 C4FSK) is <em>not</em> here: C4FSK carries 9600 bps in
    /// 10 kHz, which is the point of it.
    /// </summary>
    /// <remarks>Planning guidance, not a hard decode limit: the bench rig
    /// decodes mode 2 on its narrow (12.5 kHz) programmed channel at 38 dB SNR
    /// through an attenuator pad — adjacent-channel behaviour on air is what
    /// the 25 kHz rating protects. Sources: flashtnc release-notes.txt
    /// (upstream OBW figures) corroborated by the OARC wiki mode table
    /// (https://wiki.oarc.uk/packet:ninotnc, retrieved 2026-07-03).</remarks>
    public static readonly FrozenSet<byte> WideChannelModes = new byte[] { 0, 1, 2 }.ToFrozenSet();

    /// <summary>True when <paramref name="mode"/>'s published occupied bandwidth
    /// needs a wide (25 kHz) channel — see <see cref="WideChannelModes"/>.</summary>
    public static bool RequiresWideChannel(byte mode) => WideChannelModes.Contains(mode);

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
