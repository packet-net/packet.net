namespace Packet.Node.Core.Rigs;

/// <summary>
/// Suggests a rig manufacturer/model from a <c>/dev/serial/by-id</c> basename — the passive tier
/// of "what's plugged in?". A small curated table of case-insensitive substring patterns, each of
/// which appears in the udev descriptor <b>only</b> when the named rig is on the other end:
/// either the rig has native USB with its own vendor/product strings (<c>Icom Inc. IC-705</c>),
/// or its built-in bridge chip ships with the model programmed into the USB strings (the
/// IC-7300's CP2102). Generic bridge names (FTDI / CP210x / CH340 / PL2303 — a plain USB-UART
/// cable) deliberately suggest <b>nothing</b>: the cable says nothing about the rig behind it.
/// </summary>
/// <remarks>
/// No hamlib model numbers live here — numbers drift across hamlib versions, so the scanner
/// resolves each suggestion against the installed catalogue by name at runtime
/// (<see cref="RigModelCatalogue.ResolveNumber"/>). Manufacturer/model strings are spelled
/// exactly as hamlib's <c>rigctl -l</c> spells them so that resolution can be exact-ish.
/// Notably absent, on purpose: modern Yaesu (FT-991A / FTDX10 / FT-710 present a generic
/// "Silicon Labs CP2105 Dual USB to UART Bridge Controller"), Elecraft (K3/KX3 use plain FTDI
/// cables), and Kenwood desk rigs (TS-590/TS-890 present a generic Silicon Labs bridge) — none
/// of their descriptors identify the model.
/// </remarks>
public static class RigDescriptorSuggestions
{
    // Ordered longest-pattern-first so a more specific model token can never be shadowed by a
    // shorter one (e.g. a hypothetical "IC-970" must not swallow "IC-9700"). Each entry's comment
    // shows the real-world by-id shape the pattern was taken from.
    private static readonly (string Pattern, string Manufacturer, string ModelName)[] Table =
    [
        // usb-Icom_Inc._IC-R8600_IC-R8600_12001234_A-if00 (native USB, CDC-ACM pair)
        ("IC-R8600", "Icom", "IC-R8600"),
        // usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_IC-7300_03001234-if00-port0
        // (the IC-7300's built-in CP2102 carries the model + rig serial in the USB serial string)
        ("IC-7300", "Icom", "IC-7300"),
        // usb-Icom_Inc._IC-7610_IC-7610_98001234_A-if00 (native USB, CDC-ACM pair A/B)
        ("IC-7610", "Icom", "IC-7610"),
        // usb-Icom_Inc._IC-9700_IC-9700_12345678_A-if00 (native USB, CDC-ACM pair A/B)
        ("IC-9700", "Icom", "IC-9700"),
        // usb-Icom_Inc._IC-7100_02012345_A-if00-port0 (built-in dual UART with Icom USB strings)
        ("IC-7100", "Icom", "IC-7100"),
        // usb-Icom_Inc._IC-7200_0001234-if00-port0 (built-in bridge with Icom USB strings)
        ("IC-7200", "Icom", "IC-7200"),
        // usb-Icom_Inc._IC-9100_02012345_A-if00-port0 (built-in dual UART with Icom USB strings)
        ("IC-9100", "Icom", "IC-9100"),
        // usb-Icom_Inc._IC-705_IC-705_12345678-if00 (native USB, CDC-ACM → /dev/ttyACM*)
        ("IC-705", "Icom", "IC-705"),
        // usb-JVC_KENWOOD_TH-D74-if00 (native USB CDC-ACM → /dev/ttyACM*)
        ("TH-D74", "Kenwood", "TH-D74"),
        // usb-Kenwood_TH-D72-if00 (native USB CDC-ACM; hamlib catalogues it as the TH-D72A)
        ("TH-D72", "Kenwood", "TH-D72A"),
    ];

    /// <summary>
    /// The manufacturer/model the by-id basename identifies, or null when the descriptor is not
    /// model-distinctive (including every generic USB-UART bridge). Spelled as hamlib spells
    /// them, ready for <see cref="RigModelCatalogue.ResolveNumber"/>.
    /// </summary>
    public static (string Manufacturer, string ModelName)? Suggest(string byIdBasename)
    {
        ArgumentNullException.ThrowIfNull(byIdBasename);
        foreach (var (pattern, manufacturer, modelName) in Table)
        {
            if (byIdBasename.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return (manufacturer, modelName);
            }
        }
        return null;
    }
}
