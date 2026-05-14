namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The dsPIC chip variant fitted to a NinoTNC. The two variants need
/// *different* firmware images — flashing the wrong one bricks the
/// modem until an ICSP programmer recovers it. The major component of
/// the firmware version string (per Nino's convention from firmware
/// 2.90 onward) tells you which variant is running.
/// </summary>
public enum NinoTncChipVariant
{
    /// <summary>Unknown — firmware version not parseable / not recognised.</summary>
    Unknown = 0,

    /// <summary>dsPIC33EP256GP. Firmware major version <c>3</c>.</summary>
    Dspic33Ep256 = 3,

    /// <summary>dsPIC33EP512GP. Firmware major version <c>4</c>.</summary>
    Dspic33Ep512 = 4,
}
