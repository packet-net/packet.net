using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Scans the split-station head-end fleet — the seam behind <c>GET /api/v1/radios/headends</c>.
/// Enumerates every head-end instance (config-pinned ∪ mDNS-discovered), pulls each one's inventory,
/// reaches through the raw TCP pipe to identify + baud-lock its <b>free</b> devices, and proposes the
/// matched TNC↔radio pairs — plus any duplicate-instance-id conflicts. A test double substitutes
/// scripted results; the production <see cref="HeadEndRadioScanner"/> drives the real drivers over
/// the socket.
/// </summary>
public interface IHeadEndRadioScanner
{
    /// <summary>Scan the fleet described by <paramref name="config"/> (its head-end list + the ports
    /// whose bindings mark devices as already-bound) plus live mDNS discovery. Bounded and total:
    /// an unreachable head-end becomes an instance with <c>reachable: false</c>, never a throw.</summary>
    Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default);
}
