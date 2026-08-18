using Packet.Ax25.Sdl;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke test for figc4.6 (Awaiting V2.2 Connection - the SABME
/// handshake variant): drives every committed transition in
/// <see cref="DataLink_AwaitingV22Connection"/> through <see cref="Ax25Session"/>
/// with a recording dispatcher and stubbed decision predicates, asserting each
/// lands on its declared next state with its declared action sequence in order.
/// Data-driven off the live table via <see cref="DataLinkSmokeHarness"/>, so a
/// transition added or renamed upstream auto-appears here rather than slipping
/// past a hand-maintained list.
/// </summary>
public class DataLinkAwaitingV22ConnectionSmokeTests
{
    public static IEnumerable<object[]> AllTransitions() =>
        DataLink_AwaitingV22Connection.Transitions.Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void Transition_Fires_As_Declared(string transitionId) =>
        DataLinkSmokeHarness.AssertTransitionFiresAsDeclared(
            DataLink_AwaitingV22Connection.Transitions, "AwaitingV22Connection", transitionId);
}
