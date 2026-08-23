namespace Packet.Radio.Tait;

/// <summary>
/// The radio's self-description, assembled from the MODEL, RADIO_SERIAL and RADIO_VERSIONS
/// queries. <see cref="Versions"/> is keyed by CCDI record number: 00 model name, 01 software
/// version, 02 database versions, 03 FPGA version.
/// </summary>
public sealed record TaitRadioIdentity(
    char RuType,
    char RuModel,
    char RuTier,
    string CcdiVersion,
    string SerialNumber,
    IReadOnlyDictionary<string, string> Versions)
{
    /// <summary>Best-effort friendly product name from the RUTYPE/RUMODEL/RUTIER triple
    /// (manual §1.10.4 table; conventional-mobile rows only - this is a mobile-radio library).</summary>
    public string ProductName => (RuType, RuModel, RuTier) switch
    {
        ('1', '3', '1') => "Tait TM8105/TM8115",
        ('1', '3', '2') => "Tait TM8110",
        ('1', '3', _) => "Tait conventional mobile",
        ('1', _, _) => "Tait conventional radio",
        _ => "Tait radio",
    };

    /// <summary>
    /// The radio's band split (frequency range + UK amateur band) parsed from the
    /// <see cref="Versions"/> record <c>[00]</c> product code via <see cref="TaitBandCatalog"/>, or
    /// <c>null</c> when the radio reported no product-code record or its code is malformed / names no
    /// known split. The tuned <em>frequency</em> is not CCDI-readable, but the band <em>split</em> is -
    /// enough to label a port by amateur band (see <see cref="TaitBand.AmateurBand"/>).
    /// </summary>
    public TaitBand? Band => TaitBandCatalog.TryParse(this, out var band) ? band : null;
}

/// <summary>PA temperature reading (CCTM 047). TM8100-series radios report both a temperature
/// and the raw ADC voltage; TM8200 reports only the ADC voltage.</summary>
public sealed record TaitPaTemperature(int? Celsius, int? AdcMillivolts);

/// <summary>
/// The radio refused a transmission: PROGRESS <c>02</c> "Tx Inhibited" (MMA-00038-06 §1.10.5). The
/// message carries no reason, so neither does this - the manual's causes are over-temperature, the
/// synthesiser out of lock, the channel's power set to Off, channel activity with Tx Inhibit
/// programmed, a Tx lockout timer, or high reverse power when the codeplug's
/// <c>Override VSWR Foldback Power</c> is set.
/// </summary>
/// <param name="At">When the refusal arrived.</param>
public readonly record struct TaitTransmitInhibited(DateTimeOffset At);
