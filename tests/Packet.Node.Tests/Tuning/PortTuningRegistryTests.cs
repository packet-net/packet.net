using Packet.Node.Core.Tuning;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Tuning;

/// <summary>
/// The one-session-per-port bookkeeping (<see cref="PortTuningRegistry"/>): claim/replace rules and
/// the stop-all-on-shutdown that guarantees no port is left paused when the node goes down.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortTuningRegistryTests
{
    private static PortTuningSession Session(string portId, Action onRestore)
    {
        return new PortTuningSession(
            Guid.NewGuid().ToString("N"), portId, "12345678", TuningRole.Tuned, new DeadTuningLink(),
            new FakeStimulus(), meter: null, new TuningSessionOptions { PreBurstDelay = TimeSpan.Zero },
            restore: _ => { onRestore(); return ValueTask.CompletedTask; });
    }

    [Fact]
    public async Task Only_one_session_can_claim_a_port()
    {
        await using var registry = new PortTuningRegistry();
        var first = Session("vhf-1", () => { });
        var second = Session("vhf-1", () => { });

        registry.TryAdd(first).Should().BeTrue();
        registry.IsActive("vhf-1").Should().BeTrue();
        registry.TryAdd(second).Should().BeFalse("the port is already claimed");
        registry.Get("vhf-1").Should().BeSameAs(first);
    }

    [Fact]
    public async Task Different_ports_hold_independent_sessions()
    {
        await using var registry = new PortTuningRegistry();
        registry.TryAdd(Session("vhf-1", () => { })).Should().BeTrue();
        registry.TryAdd(Session("uhf-2", () => { })).Should().BeTrue();
        registry.IsActive("vhf-1").Should().BeTrue();
        registry.IsActive("uhf-2").Should().BeTrue();
    }

    [Fact]
    public async Task Remove_frees_the_port_only_for_the_owning_session()
    {
        await using var registry = new PortTuningRegistry();
        var owner = Session("vhf-1", () => { });
        var other = Session("vhf-1", () => { });
        registry.TryAdd(owner);

        registry.Remove(other).Should().BeFalse("a different session must not evict the owner");
        registry.IsActive("vhf-1").Should().BeTrue();
        registry.Remove(owner).Should().BeTrue();
        registry.IsActive("vhf-1").Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_the_registry_stops_and_restores_every_session()
    {
        int restores = 0;
        var registry = new PortTuningRegistry();
        registry.TryAdd(Session("vhf-1", () => Interlocked.Increment(ref restores)));
        registry.TryAdd(Session("uhf-2", () => Interlocked.Increment(ref restores)));

        await registry.DisposeAsync();

        restores.Should().Be(2, "shutdown must restore every port that had a session");
    }
}
