namespace Packet.Node.Tests.Support;

/// <summary>
/// AX.25 timing for the integration test stations + the node ports they drive.
/// </summary>
/// <remarks>
/// <para>
/// The integration tests run real <c>Ax25Listener</c> pumps over an
/// <b>in-memory</b> channel where a frame round-trips in microseconds. The thing
/// that turned a starved handshake into the ~66 s CI flake (#47) was
/// <c>Ax25Listener.ConnectAsync</c>'s backstop: it waits up to <c>(N2+1)·T1V</c>
/// for the UA, which with the spec defaults (N2 = 10, T1V = 6 s) is <b>66 s</b>.
/// When a sibling CI job starved the peer's UA past that, the connect burned the
/// whole 66 s before failing.
/// </para>
/// <para>
/// The fix lowers only the <b>retry count</b> N2 (10 → 4), leaving T1V at the
/// spec default. That bounds the connect budget at <c>(4+1)·6 s = 30 s</c> - the
/// same generous-but-bounded envelope as <see cref="Wait.DefaultBudget"/>, so a
/// genuinely stuck connect fails in 30 s instead of 66 s, while a connect that is
/// merely scheduling-delayed still completes. Critically, T1 itself stays 6 s, so
/// no spurious T1 retransmit fires inside the brief settle windows the console
/// tests assert over (e.g. the #292 banner-count test settles 100 ms then asserts
/// exactly one transmitted I-frame). Shortening T1 was tried and rejected: it both
/// risked miscounting a retransmit and made the connect budget too tight to absorb
/// real CI scheduling latency.
/// </para>
/// </remarks>
internal static class TestAx25Timing
{
    /// <summary>Test N2 (max retries) for the dial-in stations
    /// (<see cref="RemoteStation"/> / <see cref="EchoStation"/>). With the
    /// spec-default T1V this bounds their connect budget at (4+1)·6 s = 30 s.</summary>
    public static readonly int StationN2 = 4;

    /// <summary>Test N2 for a node port's <c>Ax25PortParams.N2</c> - bounds the
    /// node's own connect-OUT (the relay test) at the same 30 s envelope.</summary>
    public static readonly int NodeN2 = 4;
}
