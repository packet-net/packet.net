using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;
using Packet.Radio;
using Packet.Rig;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The kind-<c>rig</c> arm of <see cref="RadioControlFactory"/>: dispatch to the
/// <see cref="Packet.Node.Core.Rigs.IRigControlFactory"/> collaborator, the owning
/// <see cref="RigRadioControl"/> wrap (disposing the returned radio disposes the dedicated rig
/// connection it dialled), and the failure paths — a refused daemon propagates (the supervisor's
/// degrade path), a rig advertising nothing the radio seam can use is disposed rather than
/// leaked, and a missing <c>rig:</c> block (unreachable for validated config) fails clearly.
/// </summary>
[Trait("Category", "Node")]
public sealed class RadioControlFactoryRigTests
{
    private static readonly PortRadioConfig RigRadio = new() { Kind = "rig" };
    private static readonly PortRigConfig Rig = new() { Kind = "hamlib", Host = "127.0.0.1", Port = 4532 };

    [Fact]
    public async Task Kind_rig_dials_the_rig_factory_and_returns_an_owning_rig_radio_bridge()
    {
        var rig = new FakeRigControl
        {
            Capabilities = RigCapabilities.DcdRead | RigCapabilities.SignalStrengthRead | RigCapabilities.PttSet,
        };
        var rigs = new FakeRigControlFactory().Provide(rig);
        var factory = new RadioControlFactory(rigs);

        var radio = await factory.CreateAsync(RigRadio, new FakeTimeProvider(), headEndResolver: null, Rig);

        radio.Should().BeOfType<RigRadioControl>("kind rig is the rig re-presented through the radio seam");
        radio.Capabilities.Should().Be(
            RadioCapabilities.CarrierSense | RadioCapabilities.RssiRead | RadioCapabilities.TransmitterControl);
        rigs.Requests.Should().ContainSingle()
            .Which.Should().Be(Rig, "the dedicated connection dials exactly the port's rig: daemon");

        await radio.DisposeAsync();
        rig.Disposed.Should().BeTrue("ownsRig: disposing the adapter must close the dedicated rig connection");
    }

    [Fact]
    public async Task A_refused_rig_daemon_propagates_for_the_supervisor_degrade_path()
    {
        var rigs = new FakeRigControlFactory().Fault(new RigConnectionException("nothing listening on 4532"));
        var factory = new RadioControlFactory(rigs);

        var act = () => factory.CreateAsync(RigRadio, new FakeTimeProvider(), headEndResolver: null, Rig);

        await act.Should().ThrowAsync<RigConnectionException>();
    }

    [Fact]
    public async Task A_rig_with_nothing_the_radio_seam_can_use_is_disposed_not_leaked()
    {
        var rig = new FakeRigControl
        {
            // No DcdRead / SignalStrengthRead / PttSet — the bridge rejects it at construction.
            Capabilities = RigCapabilities.FrequencyGet | RigCapabilities.ModeGet,
        };
        var rigs = new FakeRigControlFactory().Provide(rig);
        var factory = new RadioControlFactory(rigs);

        var act = () => factory.CreateAsync(RigRadio, new FakeTimeProvider(), headEndResolver: null, Rig);

        await act.Should().ThrowAsync<ArgumentException>();
        rig.Disposed.Should().BeTrue("the just-dialled connection must not leak when the bridge rejects the rig");
    }

    [Fact]
    public async Task Kind_rig_without_a_rig_block_is_a_clear_wiring_failure()
    {
        // The validator makes this unreachable for real config; a bare factory call without the
        // sibling block must still fail pointedly, not NRE.
        var factory = new RadioControlFactory(new FakeRigControlFactory());

        var act = () => factory.CreateAsync(RigRadio, new FakeTimeProvider(), headEndResolver: null, rig: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rig:*");
    }
}
