namespace Packet.Node.Core.Capabilities;

/// <summary>
/// The persistence seam for the per-peer AX.25 capability cache, kept transport-free
/// so it lives in <c>Packet.Node.Core</c> (the <see cref="PeerCapabilityCache"/> service
/// drives it; the dial paths consume the cache, not the store).
/// </summary>
/// <remarks>
/// Resilient like <see cref="Auth.IRefreshTokenStore"/> and the NET/ROM routing store:
/// a backing-store fault logs and degrades (a read returns null / empty, a write is
/// dropped) - it never throws out to crash the node. Implementations open a fresh
/// pooled connection per call. Keyed by the (port, peer) pair, because capability is
/// per-link.
/// </remarks>
public interface IPeerCapabilityStore
{
    /// <summary>Insert or replace the record for its (<see cref="PeerCapabilityRecord.PortId"/>,
    /// <see cref="PeerCapabilityRecord.Peer"/>) key. A store fault is swallowed + logged.</summary>
    void Upsert(PeerCapabilityRecord record);

    /// <summary>Look up the record for one (port, peer), or null if absent / on fault.</summary>
    PeerCapabilityRecord? Find(string portId, string peer);

    /// <summary>Every persisted record (used to hydrate the hot cache on construction).
    /// Returns an empty list on fault.</summary>
    IReadOnlyList<PeerCapabilityRecord> All();

    /// <summary>Forget one (port, peer). Returns <c>true</c> if a row was removed,
    /// <c>false</c> if absent or on fault.</summary>
    bool Clear(string portId, string peer);

    /// <summary>Forget every record (operator reset). Returns the number of rows removed
    /// (0 on fault / empty table).</summary>
    int ClearAll();
}
