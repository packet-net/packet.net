namespace Packet.Ax25.Monitor;

/// <summary>
/// Where a link stands, as far as a third party listening to it can tell. Not the session
/// machine's state (<c>Ax25Session.CurrentState</c>): that is one end's own knowledge of its own
/// link, and a monitor has neither end's timers nor its variables, only what went over the air.
/// </summary>
public enum Ax25LinkState
{
    /// <summary>Only connectionless traffic (UI, XID, TEST) has been seen between the pair.</summary>
    Unconnected,

    /// <summary>A SABM or SABME has been heard and no UA or DM has answered it yet.</summary>
    Calling,

    /// <summary>The link is up: a UA answered the call, or numbered traffic was heard on a pair
    /// with no call observed (see <see cref="Ax25LinkSnapshot.Inferred"/>).</summary>
    Connected,

    /// <summary>A DISC has been heard and nothing has answered it yet.</summary>
    Disconnecting,

    /// <summary>The link is down: a UA or DM answered a DISC, a DM refused a call, a DM arrived
    /// on a link that was up, or a call or a hang-up went unanswered for longer than the
    /// observer waits (<see cref="Ax25LinkObserver.Expire"/>).</summary>
    Disconnected,
}
