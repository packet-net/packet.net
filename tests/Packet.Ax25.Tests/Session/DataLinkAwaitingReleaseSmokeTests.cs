using Packet.Ax25.Sdl;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke test for figc4.3 (Awaiting Release - the DISC/UA disconnect
/// handshake): drives every committed transition in
/// <see cref="DataLink_AwaitingRelease"/> through <see cref="Ax25Session"/> with a
/// recording dispatcher and stubbed decision predicates, asserting each lands on
/// its declared next state with its declared action sequence in order. Data-driven
/// off the live table via <see cref="DataLinkSmokeHarness"/>, so a transition
/// added or renamed upstream auto-appears here rather than slipping past a
/// hand-maintained list.
/// </summary>
public class DataLinkAwaitingReleaseSmokeTests
{
    public static IEnumerable<object[]> AllTransitions() =>
        DataLink_AwaitingRelease.Transitions.Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void Transition_Fires_As_Declared(string transitionId) =>
        DataLinkSmokeHarness.AssertTransitionFiresAsDeclared(
            DataLink_AwaitingRelease.Transitions, "AwaitingRelease", transitionId);
}
