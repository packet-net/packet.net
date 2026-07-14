using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// Scans the machine's serial devices for CAT-capable rig candidates — the node-host seam behind
/// <c>GET /api/v1/rigs/scan</c>. Passive: unlike <see cref="Radios.IRadioScanner"/> it never
/// opens or writes to a device (no <c>ID;</c> / CI-V probing in this slice) — identification is
/// by the udev by-id descriptor only. Still bounded (a timeout) and single-flight for symmetry
/// and to keep the door shut on a pathological filesystem hang. A test double substitutes
/// scripted results.
/// </summary>
public interface IRigScanner
{
    /// <summary>
    /// Enumerate the candidate devices (<c>/dev/ttyUSB*</c> + <c>/dev/ttyACM*</c>, or the
    /// <c>PACKETNET_RIG_PORTS</c> override), mark which <paramref name="current"/> config already
    /// claims, and suggest a hamlib model where the by-id descriptor identifies one. Bounded and
    /// safe: on timeout it returns what it found so far rather than hanging.
    /// </summary>
    Task<RigScan> ScanAsync(NodeConfig current, CancellationToken cancellationToken = default);
}
