using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Modems;

/// <summary>
/// Enumerates the local serial devices that could carry a KISS TNC and identifies the NinoTNCs
/// among them. The seam behind <c>GET /api/v1/setup/devices</c>; fakes drive the wizard's tests
/// and a stripped embedder may leave it unregistered (an empty scan is then honest).
/// </summary>
public interface IModemScanner
{
    /// <summary>
    /// Scan for candidate modems. <paramref name="current"/> is the live config, used to mark the
    /// devices something already claims. Bounded and single-flight: a scan never runs longer than
    /// the implementation's ceiling and two concurrent callers share one pass.
    /// </summary>
    Task<ModemScan> ScanAsync(NodeConfig current, CancellationToken cancellationToken = default);
}
