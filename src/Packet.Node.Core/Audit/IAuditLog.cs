namespace Packet.Node.Core.Audit;

/// <summary>
/// The node-wide audit sink for privileged actions. Persisted to <c>pdn.db</c>
/// (<see cref="AuditEntry"/>). <see cref="Record"/> is fire-and-forget and MUST NOT
/// throw or block the caller on a store fault — auditing can never take an action
/// path down (the same resilience contract as the other pdn.db stores).
/// </summary>
public interface IAuditLog
{
    /// <summary>Persist one audit entry (best-effort; never throws).</summary>
    void Record(AuditEntry entry);

    /// <summary>The most recent entries, newest first, bounded by <paramref name="limit"/>.</summary>
    IReadOnlyList<AuditEntry> Recent(int limit);
}
