using Packet.Node.Core.Radios;
using Packet.Radio.Tait;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The local bus scan's row mapping (<see cref="TaitRadioScanner.ToResult"/>): it must carry the
/// radio's band split (<see cref="TaitRadioIdentity.Band"/>) into <c>BandCode</c>/<c>AmateurBand</c>
/// exactly like the REMOTE head-end scan's device rows (#586 — previously dropped, so a local-attach
/// port could never be band-named). The discovery itself needs real serial hardware; the mapping is
/// pure over a synthetic identity.
/// </summary>
[Trait("Category", "Node")]
public sealed class TaitRadioScannerTests
{
    private static TaitRadioIdentity Identity(string? productCode) => new(
        RuType: '1', RuModel: '3', RuTier: '2',
        CcdiVersion: "v3.00",
        SerialNumber: "1G000123",
        Versions: productCode is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["00"] = productCode });

    [Fact]
    public void Scan_row_carries_the_band_split_like_the_remote_scan()
    {
        var found = new TaitDiscoveredRadio("/dev/ttyUSB0", 28800, Identity("TMAB12-B100_0201"));

        var row = TaitRadioScanner.ToResult(found, byIdPath: "/dev/serial/by-id/usb-x");

        row.Serial.Should().Be("1G000123");
        row.Model.Should().Be("Tait TM8110");
        row.Baud.Should().Be(28800);
        row.DevicePath.Should().Be("/dev/ttyUSB0");
        row.ByIdPath.Should().Be("/dev/serial/by-id/usb-x");
        row.BandCode.Should().Be("B1");
        row.AmateurBand.Should().Be("2m", "a B1 split covers the UK 2 m allocation");
    }

    [Fact]
    public void Scan_row_leaves_band_null_when_the_radio_reports_no_product_code()
    {
        var found = new TaitDiscoveredRadio("/dev/ttyUSB1", 28800, Identity(productCode: null));

        var row = TaitRadioScanner.ToResult(found, byIdPath: null);

        row.BandCode.Should().BeNull();
        row.AmateurBand.Should().BeNull();
    }

    [Fact]
    public void Scan_row_leaves_amateur_band_null_for_a_split_with_no_uk_allocation()
    {
        // C0 (174–225 MHz) is a known split with no amateur band — the code surfaces, the band doesn't.
        var found = new TaitDiscoveredRadio("/dev/ttyUSB2", 28800, Identity("TMAB12-C000_0201"));

        var row = TaitRadioScanner.ToResult(found, byIdPath: null);

        row.BandCode.Should().Be("C0");
        row.AmateurBand.Should().BeNull();
    }
}
