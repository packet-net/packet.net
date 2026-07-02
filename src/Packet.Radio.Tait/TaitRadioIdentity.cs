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
    /// (manual §1.10.4 table; conventional-mobile rows only — this is a mobile-radio library).</summary>
    public string ProductName => (RuType, RuModel, RuTier) switch
    {
        ('1', '3', '1') => "Tait TM8105/TM8115",
        ('1', '3', '2') => "Tait TM8110",
        ('1', '3', _) => "Tait conventional mobile",
        ('1', _, _) => "Tait conventional radio",
        _ => "Tait radio",
    };
}

/// <summary>PA temperature reading (CCTM 047). TM8100-series radios report both a temperature
/// and the raw ADC voltage; TM8200 reports only the ADC voltage.</summary>
public sealed record TaitPaTemperature(int? Celsius, int? AdcMillivolts);
