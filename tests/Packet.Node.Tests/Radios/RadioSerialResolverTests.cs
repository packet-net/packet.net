using Packet.Node.Core.Radios;
using Packet.Radio.Tait;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// <see cref="RadioSerialResolver.Match"/> — the CCDI-serial-to-device resolution behind a
/// <c>serial:</c>-bound port. The point of binding by CCDI serial is that it works where device
/// paths and <c>/dev/serial/by-id</c> don't: two CP2102 dongles that share a USB serial on
/// renumbered <c>/dev/ttyUSB*</c> paths still resolve to the right physical radio.
/// </summary>
[Trait("Category", "Node")]
public sealed class RadioSerialResolverTests
{
    private static TaitDiscoveredRadio Radio(string port, string serial)
        => new(port, 28800, new TaitRadioIdentity('1', '3', '2', "CCDI-1.0", serial,
            new Dictionary<string, string>()));

    [Fact]
    public void Match_picks_the_radio_with_the_target_ccdi_serial()
    {
        var found = new[] { Radio("/dev/ttyUSB2", "1G000111"), Radio("/dev/ttyUSB3", "1G000222") };
        RadioSerialResolver.Match(found, "1G000222")!.Port.Should().Be("/dev/ttyUSB3");
    }

    [Fact]
    public void Match_is_case_insensitive_and_trims_the_target()
    {
        var found = new[] { Radio("/dev/ttyUSB0", "1G000ABC") };
        RadioSerialResolver.Match(found, " 1g000abc ")!.Port.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void Match_returns_null_when_no_radio_has_the_serial()
        => RadioSerialResolver.Match([Radio("/dev/ttyUSB0", "1G000111")], "1G000999").Should().BeNull();

    [Fact]
    public void Match_returns_null_for_a_blank_target()
        => RadioSerialResolver.Match([Radio("/dev/ttyUSB0", "1G000111")], "   ").Should().BeNull();
}
