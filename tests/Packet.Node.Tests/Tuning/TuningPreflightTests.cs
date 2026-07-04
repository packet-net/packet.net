using Packet.Node.Core.Tuning;

namespace Packet.Node.Tests.Tuning;

/// <summary>
/// The pure, hardware-free tuning preconditions and the role/state wire mappings.
/// </summary>
[Trait("Category", "Node")]
public sealed class TuningPreflightTests
{
    [Theory]
    [InlineData("tuned", TuningRole.Tuned)]
    [InlineData("TUNED", TuningRole.Tuned)]
    [InlineData("meter", TuningRole.Meter)]
    [InlineData("Meter", TuningRole.Meter)]
    public void Role_parses_case_insensitively(string wire, TuningRole expected)
    {
        TuningPreflight.TryParseRole(wire, out var role).Should().BeTrue();
        role.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tune")]
    [InlineData(null)]
    public void An_unknown_role_token_does_not_parse(string? wire)
    {
        TuningPreflight.TryParseRole(wire, out _).Should().BeFalse();
    }

    [Fact]
    public void Role_round_trips_to_the_tune_core_wire_constants()
    {
        TuningPreflight.RoleToWire(TuningRole.Tuned).Should().Be("tuned");
        TuningPreflight.RoleToWire(TuningRole.Meter).Should().Be("meter");
    }

    [Theory]
    [InlineData(TuningSessionState.Armed, "armed")]
    [InlineData(TuningSessionState.PeerConnected, "peer-connected")]
    [InlineData(TuningSessionState.AwaitingAdjustment, "awaiting-adjustment")]
    [InlineData(TuningSessionState.Ended, "ended")]
    [InlineData(TuningSessionState.Error, "error")]
    [InlineData(TuningSessionState.Stopped, "stopped")]
    public void State_maps_to_its_wire_token(TuningSessionState state, string wire)
    {
        TuningPreflight.StateToWire(state).Should().Be(wire);
    }

    [Theory]
    [InlineData(TuningSessionState.Ended, true)]
    [InlineData(TuningSessionState.Error, true)]
    [InlineData(TuningSessionState.Stopped, true)]
    [InlineData(TuningSessionState.Armed, false)]
    [InlineData(TuningSessionState.PeerConnected, false)]
    [InlineData(TuningSessionState.AwaitingAdjustment, false)]
    public void Terminal_states_are_recognised(TuningSessionState state, bool terminal)
    {
        TuningPreflight.IsTerminal(state).Should().Be(terminal);
    }

    [Fact]
    public void A_healthy_port_with_an_eight_char_peer_can_arm()
    {
        TuningPreflight.CanArm(hasNinoTnc: true, hasTaitRadio: true, "12345678", out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void A_non_ninotnc_port_cannot_arm()
    {
        TuningPreflight.CanArm(hasNinoTnc: false, hasTaitRadio: true, "12345678", out var error).Should().BeFalse();
        error.Should().Contain("NinoTNC");
    }

    [Fact]
    public void A_port_without_a_tait_radio_cannot_arm()
    {
        TuningPreflight.CanArm(hasNinoTnc: true, hasTaitRadio: false, "12345678", out var error).Should().BeFalse();
        error.Should().Contain("Tait");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData(null)]
    public void A_wrong_length_peer_id_cannot_arm(string? peer)
    {
        TuningPreflight.CanArm(hasNinoTnc: true, hasTaitRadio: true, peer, out var error).Should().BeFalse();
        error.Should().Contain("8 characters");
    }
}
