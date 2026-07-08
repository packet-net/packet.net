using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Core.Api;

/// <summary>
/// Folds the <see cref="HeadEndHealthMonitor"/>'s live snapshot into a
/// <see cref="HeadEndScan"/> (#583): each instance the monitor tracks gains
/// <see cref="HeadEndInstanceScan.ReachableNow"/> / <see cref="HeadEndInstanceScan.LastSeen"/> so
/// the Head-ends screen's reachable badge can reflect the ~30 s background poll instead of only the
/// point-in-time scan. Pure over its inputs — an in-memory join by instance id, no probing — so the
/// <c>GET /api/v1/radios/headends</c> request path stays exactly as expensive as the scan itself.
/// </summary>
public static class HeadEndScanEnrichment
{
    /// <summary>Return <paramref name="scan"/> with live-health fields folded into every instance
    /// the monitor has data for. A null/empty <paramref name="health"/> (monitor absent in a
    /// stripped embedder, or no cycle completed yet) returns the scan unchanged — the fields stay
    /// null, an honest "no live data".</summary>
    public static HeadEndScan WithLiveHealth(HeadEndScan scan, IReadOnlyList<HeadEndHealth>? health)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (health is null || health.Count == 0 || scan.Instances.Count == 0)
        {
            return scan;
        }

        var byId = health.ToDictionary(h => h.InstanceId, StringComparer.Ordinal);
        var instances = scan.Instances
            .Select(i => byId.TryGetValue(i.InstanceId, out var h)
                ? i with { ReachableNow = h.Reachable, LastSeen = h.LastSeen }
                : i)
            .ToList();
        return scan with { Instances = instances };
    }
}
