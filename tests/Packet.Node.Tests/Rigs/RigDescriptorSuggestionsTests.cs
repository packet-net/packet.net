using Packet.Node.Core.Rigs;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// <see cref="RigDescriptorSuggestions"/>: the passive by-id tier of "what's plugged in?". A
/// descriptor that names the model (an IC-7300's reprogrammed CP2102, a native-USB IC-705) must
/// suggest it; a generic USB-UART bridge (plain FTDI / CP210x / CH340 — the cable, not the rig)
/// must suggest nothing.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigDescriptorSuggestionsTests
{
    [Theory]
    // The IC-7300's built-in CP2102 carries the model in the USB serial string.
    [InlineData("usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_IC-7300_03001234-if00-port0", "Icom", "IC-7300")]
    // Native-USB Icoms present their own vendor/product strings (CDC-ACM).
    [InlineData("usb-Icom_Inc._IC-705_IC-705_12345678-if00", "Icom", "IC-705")]
    [InlineData("usb-Icom_Inc._IC-9700_IC-9700_12345678_A-if00", "Icom", "IC-9700")]
    [InlineData("usb-Icom_Inc._IC-7610_IC-7610_98001234_A-if00", "Icom", "IC-7610")]
    // Kenwood's TH-D74 handheld enumerates with its model in the descriptor.
    [InlineData("usb-JVC_KENWOOD_TH-D74-if00", "Kenwood", "TH-D74")]
    public void Model_distinctive_descriptors_suggest_the_rig(
        string descriptor, string manufacturer, string model)
    {
        RigDescriptorSuggestions.Suggest(descriptor)
            .Should().Be((manufacturer, model));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        RigDescriptorSuggestions.Suggest("usb-icom_inc._ic-705_ic-705_12345678-if00")
            .Should().Be(("Icom", "IC-705"));
    }

    [Theory]
    // A bare FTDI CAT cable — tells you the cable's chip, nothing about the rig behind it.
    [InlineData("usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0")]
    // A generic CP2102 (no model programmed into the strings, unlike the IC-7300's).
    [InlineData("usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_0001-if00-port0")]
    // The CP2105 dual bridge modern Yaesu ship — deliberately NOT mapped to any Yaesu model.
    [InlineData("usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_01423CD5-if00-port0")]
    // A CH340 (the no-name-cable staple).
    [InlineData("usb-1a86_USB_Serial-if00-port0")]
    // A Prolific PL2303.
    [InlineData("usb-Prolific_Technology_Inc._USB-Serial_Controller-if00-port0")]
    public void Generic_bridge_descriptors_suggest_nothing(string descriptor)
    {
        RigDescriptorSuggestions.Suggest(descriptor).Should().BeNull();
    }
}
