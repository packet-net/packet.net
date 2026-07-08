using Packet.Node.Api;
using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// The two #583 exporter buckets, driven directly over synthetic state (the same pattern as
/// <see cref="RadioMetricsExporterTests"/>): the head-end fleet series
/// (<c>pdn_headend_reachable/devices/poll_failures_total</c>, formatted from
/// <see cref="HeadEndHealth"/> snapshots) and the per-port transport link-state gauge
/// (<c>pdn_port_transport_reconnecting</c>). They pin: absent-bucket-when-empty, the
/// devices-gauge omission rules (unreachable / pre-statusz daemon), the always-emitted failure
/// counter, and reconnect-supervised-ports-only emission.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndMetricsExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, double> ParseSamples(string body)
    {
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            int sp = line.LastIndexOf(' ');
            samples[line[..sp]] = double.Parse(line[(sp + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        return samples;
    }

    private static string RenderHeadEnds(params HeadEndHealth[] instances)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteHeadEndStats(w, instances);
        return w.ToString();
    }

    private static string RenderReconnecting(params (string PortId, bool Reconnecting)[] rows)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteTransportReconnecting(w, rows);
        return w.ToString();
    }

    private static HeadEndHealth Up(string id, int? bridges, long failuresTotal = 0) =>
        new(id, Reachable: true, bridges, [], LastSeen: T0, ConsecutiveFailures: 0, failuresTotal);

    private static HeadEndHealth Down(string id, int consecutive, long failuresTotal) =>
        new(id, Reachable: false, BridgeCount: 2, [], LastSeen: T0, consecutive, failuresTotal);

    // ─── the head-end fleet bucket ─────────────────────────────────────────────

    [Fact]
    public void Head_end_series_carry_the_instance_label_and_encode_reachability()
    {
        var body = RenderHeadEnds(Up("pi-shack", bridges: 2), Down("pi-attic", consecutive: 3, failuresTotal: 7));
        var s = ParseSamples(body);

        body.Should().Contain("# TYPE pdn_headend_reachable gauge");
        body.Should().Contain("# TYPE pdn_headend_poll_failures_total counter");

        s["pdn_headend_reachable{instance=\"pi-shack\"}"].Should().Be(1);
        s["pdn_headend_reachable{instance=\"pi-attic\"}"].Should().Be(0);
        s["pdn_headend_devices{instance=\"pi-shack\"}"].Should().Be(2);
        s["pdn_headend_poll_failures_total{instance=\"pi-shack\"}"].Should().Be(0, "counters emit from zero so rate() works");
        s["pdn_headend_poll_failures_total{instance=\"pi-attic\"}"].Should().Be(7);
    }

    [Fact]
    public void Devices_gauge_is_omitted_while_unreachable_and_for_a_pre_statusz_daemon()
    {
        // Down instance: its last-known bridge count must NOT render as a live devices gauge.
        var body = RenderHeadEnds(Down("pi-attic", consecutive: 1, failuresTotal: 1));
        ParseSamples(body).Should().NotContainKey("pdn_headend_devices{instance=\"pi-attic\"}");
        ParseSamples(body)["pdn_headend_reachable{instance=\"pi-attic\"}"].Should().Be(0);

        // Reachable via the healthz fallback (BridgeCount null): shape unknown, sample absent —
        // and with no instance reporting devices the whole metric (HELP/TYPE included) is absent.
        var fallback = RenderHeadEnds(Up("old-pi", bridges: null));
        fallback.Should().NotContain("pdn_headend_devices");
        ParseSamples(fallback)["pdn_headend_reachable{instance=\"old-pi\"}"].Should().Be(1);
    }

    [Fact]
    public void Head_end_bucket_is_absent_with_no_monitored_instances()
    {
        RenderHeadEnds().Should().BeEmpty();

        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteHeadEndStats(w, instances: null);   // stripped embedder: no monitor at all
        w.ToString().Should().BeEmpty();
    }

    // ─── the per-port transport link-state gauge ───────────────────────────────

    [Fact]
    public void Transport_reconnecting_gauge_reflects_each_supervised_port()
    {
        var body = RenderReconnecting(("vhf", false), ("uhf", true));
        var s = ParseSamples(body);

        body.Should().Contain("# TYPE pdn_port_transport_reconnecting gauge");
        s["pdn_port_transport_reconnecting{port=\"vhf\"}"].Should().Be(0);
        s["pdn_port_transport_reconnecting{port=\"uhf\"}"].Should().Be(1);
    }

    [Fact]
    public void Transport_reconnecting_bucket_is_absent_with_no_supervised_ports()
    {
        // Local-serial / AXUDP-only nodes have no reconnect decorator — the bucket must not
        // appear at all (no misleading always-0 series for ports that can't reconnect).
        RenderReconnecting().Should().BeEmpty();
    }
}
