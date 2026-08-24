using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The sentence a port's "no radio attached" refusal carries when the supervisor knows why there
/// is none. A port whose radio failed to open still serves traffic, so every feature that needs the
/// radio - a test transmission, a hail - refuses with the same flat "this port has no Tait", while
/// the actual reason (the control channel never opened, and why) sat in the journal where nobody
/// looking at the panel could see it.
/// </summary>
[Trait("Category", "Node")]
public sealed class RadioControlsWhyNoRadioTests
{
    private const string Port = "vhf-1";

    [Fact]
    public void A_port_degraded_without_its_radio_explains_itself()
    {
        var health = new StubHealth(new PortHealth
        {
            Id = Port,
            State = PortState.Degraded,
            Since = DateTimeOffset.UnixEpoch,
            Degraded = [PortComponents.Radio],
            LastError = "radio (tait-ccdi on serial:19925328): no tait-ccdi radio with CCDI serial "
                + "'19925328' answered at 28800 baud.",
        });

        string why = RadioControls.WhyNoRadio(health, Port);

        why.Should().Contain("DEGRADED").And.Contain("19925328").And.Contain("restart the port");
    }

    [Fact]
    public void A_healthy_port_with_no_radio_configured_adds_nothing()
    {
        // Nothing to explain: the operator simply has not attached a radio, which the refusal
        // itself already says. A second sentence here would be noise.
        var health = new StubHealth(new PortHealth
        {
            Id = Port,
            State = PortState.Up,
            Since = DateTimeOffset.UnixEpoch,
        });

        RadioControls.WhyNoRadio(health, Port).Should().BeEmpty();
    }

    [Fact]
    public void A_port_degraded_on_something_else_does_not_blame_the_radio()
    {
        var health = new StubHealth(new PortHealth
        {
            Id = Port,
            State = PortState.Degraded,
            Since = DateTimeOffset.UnixEpoch,
            Degraded = [PortComponents.Rig],
            LastError = "rig (hamlib at 127.0.0.1:4532): connection refused",
        });

        RadioControls.WhyNoRadio(health, Port).Should().BeEmpty();
    }

    [Fact]
    public void No_health_view_at_all_is_not_a_crash()
    {
        RadioControls.WhyNoRadio(null, Port).Should().BeEmpty();
        RadioControls.WhyNoRadio(new StubHealth(null), Port).Should().BeEmpty();
    }

    private sealed class StubHealth(PortHealth? health) : IPortHealthView
    {
        public PortHealth? GetHealth(string id) => health;

        public IReadOnlyList<PortHealth> Snapshot() => health is null ? [] : [health];
    }
}
