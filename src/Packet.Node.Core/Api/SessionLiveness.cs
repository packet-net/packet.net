using Packet.Ax25.Session;

namespace Packet.Node.Core.Api;

/// <summary>
/// The one definition of "this AX.25 session is live" for every node-level projection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ax25Listener.ActiveSessions"/> is an <em>engine cache</em>, not a live set: the
/// listener deliberately keeps a session object after the link goes down (state
/// <c>Disconnected</c>) so a returning peer resumes against the same state machine, and only
/// evicts it LRU-style when <c>MaxCachedPeers</c> is exceeded. That is right for the engine and
/// wrong for anything reported to a human: counting the cache made the dashboard's "Active
/// sessions" climb forever and left disconnected rows in <c>GET /api/v1/sessions</c> (review
/// item C052, #694).
/// </para>
/// <para>
/// A link counts as live in exactly the two connected-mode states that can still carry data:
/// <c>Connected</c>, and <c>TimerRecovery</c> (an established link whose T1 is retrying, still
/// up, just unacknowledged). Everything else, including <c>Disconnected</c> and the transient
/// handshake states, is not a circuit an operator would call open. This is the discipline
/// <c>NodeOarcStateSource</c> already used; every other projection now shares it.
/// </para>
/// </remarks>
public static class SessionLiveness
{
    /// <summary>True when <paramref name="state"/> is an established connected-mode state
    /// (<c>Connected</c> or <c>TimerRecovery</c>).</summary>
    public static bool IsLive(string? state) => state is "Connected" or "TimerRecovery";

    /// <summary>True when the session is an established connected-mode circuit. See
    /// <see cref="IsLive(string?)"/>.</summary>
    public static bool IsLive(Ax25Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return IsLive(session.CurrentState);
    }
}
