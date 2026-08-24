using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Radio.Tait;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// Resolving a <c>serial:</c>-bound Tait to the device it is actually on, and - the part that
/// matters at 2am - what the failure says when it is on none of them. The old message counted the
/// radios it found and called them "probed port(s)", so a radio that was mid-reboot after a
/// codeplug write reported "found among 0 probed port(s)", which reads as "this machine has no
/// serial ports" and sends the operator looking in the wrong place entirely.
/// </summary>
[Trait("Category", "Node")]
public sealed class TaitEndpointResolverTests
{
    private static PortRadioConfig SerialBound(string serial) =>
        new() { Kind = RadioKinds.TaitCcdi, Serial = serial, Baud = 28800 };

    [Fact]
    public async Task A_path_bound_radio_resolves_to_itself_without_probing_anything()
    {
        var radio = new PortRadioConfig { Kind = RadioKinds.TaitCcdi, Port = "/dev/ttyUSB4", Baud = 19200 };

        var (port, baud) = await TaitEndpointResolver.ResolveAsync(radio);

        port.Should().Be("/dev/ttyUSB4");
        baud.Should().Be(19200);
    }

    [Fact]
    public async Task A_radio_that_is_on_none_of_them_names_the_ports_that_were_probed()
    {
        // Nothing answers on these (they do not exist), which is the same answer a radio still
        // rebooting after a codeplug write gives.
        using var _ = TaitPortsOverride("/dev/pts/240,/dev/pts/241");

        var act = async () => await TaitEndpointResolver.ResolveAsync(
            SerialBound("19925328"));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should()
            .Contain("19925328")
            .And.Contain("Probed 2 port(s)")
            .And.Contain("/dev/pts/240, /dev/pts/241")
            .And.Contain("restarting", "a radio is silent for a few seconds after a codeplug write");
    }

    [Fact]
    public async Task A_machine_with_no_candidate_ports_at_all_says_that_instead()
    {
        // A lone separator: an override that is set but names nothing, which is how a machine with
        // no /dev/ttyUSB* at all looks to the scan.
        using var _ = TaitPortsOverride(",");

        var act = async () => await TaitEndpointResolver.ResolveAsync(
            SerialBound("19925328"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no candidate serial ports to probe");
    }

    private static Restore TaitPortsOverride(string value)
    {
        Environment.SetEnvironmentVariable(TaitRadioPortDiscovery.PortsOverrideEnvVar, value);
        return new Restore();
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(TaitRadioPortDiscovery.PortsOverrideEnvVar, null);
    }
}
