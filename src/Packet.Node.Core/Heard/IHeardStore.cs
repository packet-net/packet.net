namespace Packet.Node.Core.Heard;

/// <summary>
/// The persistence seam for the MHeard log (the <c>MH</c> verb's backing store, #454), kept
/// transport-free so it lives in <c>Packet.Node.Core</c>. The <see cref="HeardLog"/> service
/// drives it; the telemetry tap feeds the service, not the store.
/// </summary>
/// <remarks>
/// Resilient like <see cref="Capabilities.IPeerCapabilityStore"/> and the NET/ROM routing
/// store: a backing-store fault logs and degrades (a read returns empty, a write is dropped) —
/// it never throws out to crash the node. Implementations open a fresh pooled connection per
/// call. Keyed by the (port, callsign) pair, because a hearing is per-link.
/// </remarks>
public interface IHeardStore
{
    /// <summary>Insert or update the row for its (<see cref="HeardEntry.PortId"/>,
    /// <see cref="HeardEntry.Callsign"/>) key. A store fault is swallowed + logged.</summary>
    void Upsert(HeardEntry entry);

    /// <summary>Every persisted row (used to hydrate the hot log on construction).
    /// Returns an empty list on fault.</summary>
    IReadOnlyList<HeardEntry> All();

    /// <summary>Forget one (port, callsign). Returns <c>true</c> if a row was removed,
    /// <c>false</c> if absent or on fault.</summary>
    bool Clear(string portId, string callsign);

    /// <summary>Forget every row (operator reset). Returns the number of rows removed.</summary>
    int ClearAll();

    /// <summary>
    /// Age-out / cap the stored log: delete rows last heard before <paramref name="olderThan"/>,
    /// then, for any port still over <paramref name="maxPerPort"/> rows, delete the oldest-heard
    /// rows on that port down to the cap. Returns the number of rows deleted. A store fault
    /// returns 0 (the prune is best-effort, like every other op).
    /// </summary>
    int Prune(DateTimeOffset olderThan, int maxPerPort);
}
