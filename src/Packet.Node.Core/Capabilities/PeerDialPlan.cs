namespace Packet.Node.Core.Capabilities;

/// <summary>
/// Why we are dialling, which sets the optimistic default when nothing is cached.
/// </summary>
public enum PeerDialPolicy
{
    /// <summary>Node-to-node interlink (the NET/ROM backbone). Conservative default -
    /// start mod-8 unless we have learned the neighbour does extended, because a stalled
    /// SABME probe to a v2.0-only backbone neighbour costs the whole link a retry cycle.</summary>
    Interlink,

    /// <summary>A user-initiated connect. Optimistic default - offer SABME unless we have
    /// learned the peer refuses it, since a user connect to an unknown peer is worth the
    /// one-time extended probe.</summary>
    UserConnect,
}

/// <summary>
/// The dial decision <see cref="PeerCapabilityCache.PlanDial"/> hands back: whether to
/// offer the v2.2 extended (SABME) setup, and whether to send a pre-connect XID to
/// discover SREJ. On the extended path the pre-connect XID is moot (XID negotiation
/// rides the extended setup), so <see cref="PreConnectXid"/> is <c>false</c> there.
/// </summary>
public readonly record struct PeerDialPlan(bool Extended, bool PreConnectXid);
