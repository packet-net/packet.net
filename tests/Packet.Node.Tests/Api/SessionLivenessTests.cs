using Packet.Node.Core.Api;

namespace Packet.Node.Tests.Api;

/// <summary>
/// The one predicate every node-level session projection shares (#727 item 8).
/// </summary>
/// <remarks>
/// It shipped in node-v0.41.0 admitting only <c>Connected</c> and <c>TimerRecovery</c>, which
/// hid every handshake and release state from <c>GET /sessions</c>, the <c>/status</c>
/// active-session count and the per-port counts. A link retrying SABM for N2 x T1 - minutes on
/// a slow RF path - showed the operator an idle node while the port was transmitting, and a
/// session wedged waiting for a UA to its DISC could not be found in order to be deleted:
/// <c>FindSession</c> never filtered by liveness, so the list and the actions disagreed.
/// </remarks>
[Trait("Category", "Node")]
public sealed class SessionLivenessTests
{
    [Theory]
    [InlineData("Connected")]
    [InlineData("TimerRecovery")]
    [InlineData("AwaitingConnection")]
    [InlineData("AwaitingV22Connection")]
    [InlineData("AwaitingRelease")]
    public void Every_state_but_disconnected_is_published(string state) =>
        SessionLiveness.IsLive(state).Should().BeTrue(
            $"a session in {state} is a circuit the operator must be able to see and act on");

    [Fact]
    public void The_engines_cached_dead_peer_is_the_only_thing_filtered_out() =>
        // Ax25Listener.ActiveSessions is an LRU peer CACHE that keeps a Disconnected session
        // object until eviction, which is why the filter exists at all (C052, #694).
        SessionLiveness.IsLive("Disconnected").Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_state_is_not_live(string? state) =>
        SessionLiveness.IsLive(state).Should().BeFalse();

    [Fact]
    public void The_match_is_ordinal_and_case_sensitive() =>
        // The states are the SDL table's own names; a near-miss must not silently mean "dead".
        SessionLiveness.IsLive("disconnected").Should().BeTrue(
            "only the exact state name 'Disconnected' is the engine's cached-dead marker");
}
