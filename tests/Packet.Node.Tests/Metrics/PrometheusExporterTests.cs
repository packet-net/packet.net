using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// Unit tests for the Prometheus <c>/metrics</c> exporter (#457). Drive
/// <see cref="PdnMetricsApi.Render"/> over a directly-built <see cref="NodeHostedService"/>
/// whose <see cref="NodeHostedService.Telemetry"/> is seeded with synthetic frames — no
/// Kestrel, no modem. They pin three things the acceptance criteria call out: the exposition
/// format is valid (HELP/TYPE headers, every metric <c>pdn_*</c>), metric values track the
/// underlying telemetry counters, and label cardinality is bounded (per-port only — never a
/// per-remote-callsign series, even with many peers heard on one port).
/// </summary>
[Trait("Category", "Node")]
public sealed class PrometheusExporterTests
{
    private const string Port = "vhf";
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");

    private static Ax25FrameEventArgs Rx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Received, Timestamp = at };

    private static Ax25FrameEventArgs Tx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = at };

    private static DateTimeOffset At(int s) => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(s);

    private static (NodeHostedService Host, TestConfigProvider Config, FakeTimeProvider Clock) BuildHost()
    {
        var clock = new FakeTimeProvider(At(0));
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = Local.ToString(), Alias = "LONDON" },
            Ports = [new PortConfig { Id = Port, Enabled = false, Transport = new KissTcpTransport { Host = "x", Port = 1 } }],
        });
        // Supervisor is null until ExecuteAsync — fine: the projections null-tolerate it and the
        // configured-but-disabled port reports state "disabled" with live frame totals from the tap.
        var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);
        return (host, config, clock);
    }

    // Parse the exposition text into metric{labelset} -> value, ignoring HELP/TYPE lines.
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
            sp.Should().BeGreaterThan(0, "every sample line is `series value`");
            var series = line[..sp];
            var value = double.Parse(line[(sp + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            samples[series] = value;
        }
        return samples;
    }

    [Fact]
    public void Exposition_is_well_formed_with_help_type_headers_and_a_pdn_namespace()
    {
        var (host, config, clock) = BuildHost();

        var body = PdnMetricsApi.Render(host, config, clock, traffic: null);

        // Every non-comment, non-blank line is a sample whose name starts pdn_.
        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            line.Should().StartWith("pdn_", "all series live in the pdn_ namespace");
        }

        // HELP + TYPE precede a known metric.
        body.Should().Contain("# HELP pdn_uptime_seconds ");
        body.Should().Contain("# TYPE pdn_uptime_seconds gauge");
        body.Should().Contain("# TYPE pdn_port_frames_received_total counter");

        // build_info is the value-1 info gauge carrying version/callsign in labels.
        body.Should().Contain("pdn_build_info{");
        body.Should().Contain("callsign=\"M0LTE-1\"");
    }

    [Fact]
    public void Node_health_metrics_track_the_configured_node()
    {
        var (host, config, clock) = BuildHost();

        var samples = ParseSamples(PdnMetricsApi.Render(host, config, clock, traffic: null));

        samples["pdn_ports_total"].Should().Be(1);   // one configured port
        samples["pdn_ports_up"].Should().Be(0);      // it is disabled
        samples["pdn_sessions"].Should().Be(0);
        // The info gauge is always 1.
        samples.Keys.Should().Contain(k => k.StartsWith("pdn_build_info{", StringComparison.Ordinal));
        samples.First(k => k.Key.StartsWith("pdn_build_info{", StringComparison.Ordinal)).Value.Should().Be(1);
        // Process stats are present.
        samples.Should().ContainKey("pdn_process_threads");
        samples.Should().ContainKey("pdn_process_resident_memory_bytes");
    }

    [Fact]
    public void Per_port_frame_and_byte_counters_track_the_telemetry_tap()
    {
        var (host, config, clock) = BuildHost();
        var peer = Callsign.Parse("G7XYZ-2");

        // 2 RX (one 2-byte I-frame, one RR), 1 TX (3-byte I-frame), plus a REJ + SREJ in.
        host.Telemetry.Observe(Port, Rx(Ax25Frame.I(Local, peer, nr: 0, ns: 0, "ab"u8), At(0)));
        host.Telemetry.Observe(Port, Rx(Ax25Frame.Rr(Local, peer, nr: 1, isCommand: false), At(1)));
        host.Telemetry.Observe(Port, Tx(Ax25Frame.I(peer, Local, nr: 1, ns: 0, "cde"u8), At(2)));
        host.Telemetry.Observe(Port, Rx(Ax25Frame.Rej(Local, peer, nr: 1, isCommand: false), At(3)));
        host.Telemetry.Observe(Port, Rx(Ax25Frame.Srej(Local, peer, nr: 2, isCommand: false), At(4)));

        var samples = ParseSamples(PdnMetricsApi.Render(host, config, clock, traffic: null));

        samples[$"pdn_port_frames_received_total{{port=\"{Port}\"}}"].Should().Be(4);   // I+RR+REJ+SREJ
        samples[$"pdn_port_frames_transmitted_total{{port=\"{Port}\"}}"].Should().Be(1);
        samples[$"pdn_port_info_bytes_received_total{{port=\"{Port}\"}}"].Should().Be(2);  // only the inbound I info
        samples[$"pdn_port_info_bytes_transmitted_total{{port=\"{Port}\"}}"].Should().Be(3);
        samples[$"pdn_port_rej_total{{port=\"{Port}\"}}"].Should().Be(1);
        samples[$"pdn_port_srej_total{{port=\"{Port}\"}}"].Should().Be(1);
    }

    [Fact]
    public void Label_cardinality_is_bounded_to_the_port_even_with_many_peers()
    {
        var (host, config, clock) = BuildHost();

        // 50 distinct remote callsigns heard on the one port. A naive per-link exporter would
        // emit 50 series per metric (unbounded by remote callsign); ours sums them to one port label.
        for (int i = 0; i < 50; i++)
        {
            var peer = new Callsign("G" + (char)('A' + i % 26) + "X", (byte)(i % 16));
            host.Telemetry.Observe(Port, Rx(Ax25Frame.I(Local, peer, nr: 0, ns: 0, "ab"u8), At(i)));
        }

        var body = PdnMetricsApi.Render(host, config, clock, traffic: null);
        var samples = ParseSamples(body);

        // No series carries a peer/callsign label anywhere in /metrics.
        body.Should().NotContain("peer=\"");
        body.Should().NotContain("callsign=\"G");   // build_info has the NODE callsign only

        // Exactly one received-frames series for the port, and it summed all 50 peers.
        var rxSeries = samples.Keys.Where(k => k.StartsWith("pdn_port_frames_received_total", StringComparison.Ordinal)).ToList();
        rxSeries.Should().ContainSingle();
        samples[rxSeries[0]].Should().Be(50);

        // The whole document's series count is bounded (a small constant), not ~O(peers).
        samples.Count.Should().BeLessThan(60);
    }

    [Fact]
    public void Forwarding_bucket_is_zero_on_an_endpoint_node_and_breaks_drops_down_by_reason()
    {
        var (host, config, clock) = BuildHost();

        var body = PdnMetricsApi.Render(host, config, clock, traffic: null);
        var samples = ParseSamples(body);

        samples["pdn_netrom_forwarded_frames_total"].Should().Be(0);
        samples["pdn_netrom_forwarded_bytes_total"].Should().Be(0);
        // Three bounded reason values, all zero (no NET/ROM forwarding configured).
        samples["pdn_netrom_forward_drops_total{reason=\"ttl_expired\"}"].Should().Be(0);
        samples["pdn_netrom_forward_drops_total{reason=\"looped\"}"].Should().Be(0);
        samples["pdn_netrom_forward_drops_total{reason=\"no_route\"}"].Should().Be(0);
    }
}
