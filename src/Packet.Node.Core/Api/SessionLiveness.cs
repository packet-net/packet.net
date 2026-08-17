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
/// The predicate is therefore "anything but <c>Disconnected</c>": <c>Connected</c>,
/// <c>TimerRecovery</c> (an established link whose T1 is retrying, still up, just
/// unacknowledged), and the transient <c>AwaitingConnection</c> /
/// <c>AwaitingV22Connection</c> / <c>AwaitingRelease</c> states all publish. Only the cached
/// dead peer is filtered out.
/// </para>
/// <para>
/// It admitted only <c>Connected</c> and <c>TimerRecovery</c> when it shipped in node-v0.41.0,
/// which hid every handshake and release state from <c>GET /sessions</c>, the <c>/status</c>
/// active-session count and the per-port counts (#727 item 8). A link retrying SABM for
/// N2 x T1 - minutes on a slow RF path - showed the operator an idle node while the port was
/// transmitting, and a session wedged waiting for a UA to its DISC could not be found to be
/// deleted. <c>FindSession</c> never filtered by liveness, so the list and the actions
/// disagreed: an id the API refused to publish was still actionable. Publishing everything
/// except <c>Disconnected</c> makes them agree again, and the row's <c>state</c> field is what
/// tells a human "establishing" from "up".
/// </para>
/// </remarks>
public static class SessionLiveness
{
    /// <summary>The engine's cached-but-dead state: a peer object the listener keeps so a
    /// returning station resumes against the same state machine.</summary>
    private const string DisconnectedState = "Disconnected";

    /// <summary>True when <paramref name="state"/> is any state other than
    /// <c>Disconnected</c> (a null/blank state is not live).</summary>
    public static bool IsLive(string? state) =>
        !string.IsNullOrEmpty(state) && !string.Equals(state, DisconnectedState, StringComparison.Ordinal);

    /// <summary>True when the session is anything but a cached <c>Disconnected</c> peer. See
    /// <see cref="IsLive(string?)"/>.</summary>
    public static bool IsLive(Ax25Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return IsLive(session.CurrentState);
    }
}
