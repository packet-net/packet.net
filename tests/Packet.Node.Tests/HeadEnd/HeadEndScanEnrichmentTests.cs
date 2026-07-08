using Packet.Node.Core.Api;
using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// <see cref="HeadEndScanEnrichment.WithLiveHealth"/> (#583) — the in-memory join that folds the
/// background poller's snapshot into <c>GET /api/v1/radios/headends</c>: matched instances gain
/// <c>reachableNow</c>/<c>lastSeen</c>, unmatched instances (and a scan with no monitor data at
/// all) keep them null — the fields say "live poller view", never a guess.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndScanEnrichmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private static HeadEndInstanceScan Instance(string id) =>
        new(id, Host: "pi.test", HttpPort: 7300, Source: "config",
            Reachable: true, Error: null, Devices: [], ProposedPairs: [], PairingAmbiguous: false);

    private static HeadEndHealth Health(string id, bool reachable, DateTimeOffset? lastSeen) =>
        new(id, reachable, BridgeCount: 1, Bridges: [], lastSeen, ConsecutiveFailures: 0, PollFailuresTotal: 0);

    [Fact]
    public void Matched_instances_gain_reachableNow_and_lastSeen_unmatched_stay_null()
    {
        var scan = new HeadEndScan([Instance("pi-shack"), Instance("pi-attic")], []);

        var enriched = HeadEndScanEnrichment.WithLiveHealth(
            scan, [Health("pi-shack", reachable: false, lastSeen: T0)]);

        var shack = enriched.Instances.Single(i => i.InstanceId == "pi-shack");
        shack.ReachableNow.Should().BeFalse("the poller's live verdict wins over the scan-time one");
        shack.LastSeen.Should().Be(T0);
        // The scan-time result is untouched — the two reachability views coexist.
        shack.Reachable.Should().BeTrue();

        var attic = enriched.Instances.Single(i => i.InstanceId == "pi-attic");
        attic.ReachableNow.Should().BeNull("no monitor data for this instance — an honest null, not a guess");
        attic.LastSeen.Should().BeNull();
    }

    [Fact]
    public void No_monitor_data_returns_the_scan_unchanged()
    {
        var scan = new HeadEndScan([Instance("pi-shack")], []);

        HeadEndScanEnrichment.WithLiveHealth(scan, health: null).Should().BeSameAs(scan);
        HeadEndScanEnrichment.WithLiveHealth(scan, health: []).Should().BeSameAs(scan);

        scan.Instances[0].ReachableNow.Should().BeNull();
        scan.Instances[0].LastSeen.Should().BeNull();
    }

    [Fact]
    public void Conflicts_and_device_rows_pass_through_untouched()
    {
        var conflict = new HeadEndConflict("dup", ["a:7300", "b:7300"]);
        var device = new HeadEndDeviceScan("nino0", HeadEndDeviceKind.NinoTnc,
            Model: null, Version: "3.44", Serial: null, Baud: 57600, Free: true);
        var scan = new HeadEndScan([Instance("pi-shack") with { Devices = [device] }], [conflict]);

        var enriched = HeadEndScanEnrichment.WithLiveHealth(scan, [Health("pi-shack", true, T0)]);

        enriched.Conflicts.Should().ContainSingle().Which.Should().BeSameAs(conflict);
        enriched.Instances.Single().Devices.Should().ContainSingle().Which.Should().BeSameAs(device);
    }
}
