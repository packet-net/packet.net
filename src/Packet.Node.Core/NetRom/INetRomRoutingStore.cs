using Packet.NetRom.Routing;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// A persisted routing snapshot plus the instant it was saved. The saved-at stamp
/// lets the loader age the routes by the elapsed downtime (so a long-dead route is
/// not restored at full obsolescence) - see
/// <see cref="NetRomRoutingTable.Restore"/>.
/// </summary>
/// <param name="Snapshot">The persisted routing table.</param>
/// <param name="SavedAt">When the snapshot was written.</param>
public readonly record struct PersistedRouting(NetRomRoutingSnapshot Snapshot, DateTimeOffset SavedAt);

/// <summary>
/// Persistence seam for the learned NET/ROM routing table - the node host's durable
/// store for what it has heard, so a restart does not lose the network topology.
/// </summary>
/// <remarks>
/// The protocol-side <see cref="NetRomRoutingTable"/> stays storage-agnostic: it
/// exports a <see cref="NetRomRoutingSnapshot"/> (<see cref="NetRomRoutingTable.Snapshot"/>)
/// and re-imports one (<see cref="NetRomRoutingTable.Restore"/>); this seam moves that
/// snapshot to and from durable storage. Implementations <b>must be resilient</b> - a
/// persistence fault must degrade to in-memory operation, never fault the node - so
/// <see cref="Load"/> returns <c>null</c> on any failure and <see cref="Save"/>
/// swallows + logs. The default node-host implementation is
/// <see cref="SqliteNetRomRoutingStore"/> (a <c>pdn.db</c> SQLite file).
/// </remarks>
public interface INetRomRoutingStore
{
    /// <summary>Load the last persisted routing snapshot, or <c>null</c> if none is
    /// stored (or the store could not be read).</summary>
    PersistedRouting? Load();

    /// <summary>Persist <paramref name="snapshot"/> as the current routing table,
    /// stamped <paramref name="savedAt"/>; replaces any previously stored snapshot.</summary>
    void Save(NetRomRoutingSnapshot snapshot, DateTimeOffset savedAt);
}
