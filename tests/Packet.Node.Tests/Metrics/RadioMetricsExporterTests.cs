using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Node.Api;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Heard;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// Unit tests for the two radio-observability buckets the exporter grew for Phase 10 (radio-control
/// metrics into <c>/metrics</c>): the per-port <c>pdn_radio_*</c> health series (driven directly over
/// synthetic <see cref="RadioStatus"/> records, the same read model <c>/api/v1/radios</c> serves) and
/// the per-partner <c>pdn_link_snr_db{port,peer}</c> gauge (driven over a seeded <see cref="HeardLog"/>).
/// They pin: attached-only emission, the connection-state gauge encoding, null-reading omission, and
/// the deliberate per-callsign SNR label (one series per heard partner with a measured SNR — no cap).
/// </summary>
[Trait("Category", "Node")]
public sealed class RadioMetricsExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");

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
            var series = line[..sp];
            samples[series] = double.Parse(line[(sp + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        return samples;
    }

    private static string RenderRadios(params RadioStatus[] radios)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteRadioStats(w, radios);
        return w.ToString();
    }

    private static string RenderLinkSnr(HeardLog? heard)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteLinkSnr(w, heard);
        return w.ToString();
    }

    private static string RenderLinkPreData(HeardLog? heard)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteLinkPreDataCarrier(w, heard);
        return w.ToString();
    }

    private static RadioStatus Attached(string port, string state, bool? busy, RadioHealth? health) =>
        new(PortId: port, Attached: true, Kind: "tait-ccdi", ControlPort: "/dev/ttyUSB0", Serial: null,
            Identity: null, ConnectionState: state, ChannelBusy: busy, Health: health);

    // ─── per-port radio health ─────────────────────────────────────────────────

    [Fact]
    public void Radio_health_series_track_the_attached_radio_and_omit_a_configured_but_detached_one()
    {
        var health = new RadioHealth(
            RssiDbm: -72.5f, AveragedRssiDbm: -74f, PaTemperatureC: 42,
            ForwardTrendMillivolts: 1200, ReverseTrendMillivolts: 150,
            ReverseForwardRatio: 0.125, SampleAt: T0);

        var body = RenderRadios(
            Attached("a", "healthy", busy: true, health),
            new RadioStatus("b", Attached: false, "tait-ccdi", null, null, null, "unknown", null, null));
        var s = ParseSamples(body);

        // Exposition is well-formed and every series is port-labelled.
        body.Should().Contain("# TYPE pdn_radio_rssi_dbm gauge");

        s["pdn_radio_connection_state{port=\"a\"}"].Should().Be(1);         // healthy
        s["pdn_radio_channel_busy{port=\"a\"}"].Should().Be(1);             // busy
        s["pdn_radio_rssi_dbm{port=\"a\"}"].Should().Be(-72.5);
        s["pdn_radio_rssi_averaged_dbm{port=\"a\"}"].Should().Be(-74);
        s["pdn_radio_pa_temperature_celsius{port=\"a\"}"].Should().Be(42);
        s["pdn_radio_forward_trend_millivolts{port=\"a\"}"].Should().Be(1200);
        s["pdn_radio_reverse_trend_millivolts{port=\"a\"}"].Should().Be(150);
        s["pdn_radio_reverse_forward_ratio{port=\"a\"}"].Should().Be(0.125);

        // The configured-but-not-attached radio contributes NOTHING.
        body.Should().NotContain("port=\"b\"");
    }

    [Fact]
    public void Connection_state_encodes_healthy_faulted_unknown_and_null_readings_are_omitted()
    {
        var body = RenderRadios(
            Attached("a", "healthy", busy: null, health: null),
            Attached("b", "faulted", busy: false, health: null),
            Attached("c", "unknown", busy: true, health: null));
        var s = ParseSamples(body);

        s["pdn_radio_connection_state{port=\"a\"}"].Should().Be(1);
        s["pdn_radio_connection_state{port=\"b\"}"].Should().Be(0);
        s["pdn_radio_connection_state{port=\"c\"}"].Should().Be(-1);

        // channel_busy is omitted where unknown (null), present where reported.
        s.Should().NotContainKey("pdn_radio_channel_busy{port=\"a\"}");
        s["pdn_radio_channel_busy{port=\"b\"}"].Should().Be(0);
        s["pdn_radio_channel_busy{port=\"c\"}"].Should().Be(1);

        // No radio produced a health sample → the health series (HELP/TYPE included) are absent.
        body.Should().NotContain("pdn_radio_rssi_dbm");
        body.Should().NotContain("pdn_radio_pa_temperature_celsius");
    }

    [Fact]
    public void No_attached_radio_emits_no_radio_bucket_at_all()
    {
        // Only a detached radio → the whole pdn_radio_ bucket is absent (a node with no radios reads
        // exactly as it did before this bucket existed).
        var body = RenderRadios(
            new RadioStatus("a", Attached: false, "tait-ccdi", null, null, null, "unknown", null, null));
        body.Should().NotContain("pdn_radio_");
    }

    // ─── per-partner SNR ───────────────────────────────────────────────────────

    [Fact]
    public void Link_snr_emits_one_gauge_per_heard_partner_with_a_measured_snr()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "G0ABC-1", T0, rssiDbm: -80f, snrDb: 20f);
        log.Record("vhf", "G7XYZ-2", T0.AddSeconds(1), rssiDbm: -95f, snrDb: 6.5f);
        // Heard, but only an RSSI reading (no SNR) — must NOT appear in the SNR gauge.
        log.Record("vhf", "M7RSS-3", T0.AddSeconds(2), rssiDbm: -70f, snrDb: null);
        // Heard on a radio-less port (neither reading) — must NOT appear.
        log.Record("hf", "M7NIL-4", T0.AddSeconds(3));

        var body = RenderLinkSnr(log);
        var s = ParseSamples(body);

        body.Should().Contain("# TYPE pdn_link_snr_db gauge");
        s["pdn_link_snr_db{port=\"vhf\",peer=\"G0ABC-1\"}"].Should().Be(20);
        s["pdn_link_snr_db{port=\"vhf\",peer=\"G7XYZ-2\"}"].Should().Be(6.5);

        // Exactly the two SNR-bearing partners — the rssi-only and no-radio partners are excluded.
        s.Keys.Where(k => k.StartsWith("pdn_link_snr_db", StringComparison.Ordinal)).Should().HaveCount(2);
        body.Should().NotContain("M7RSS-3");
        body.Should().NotContain("M7NIL-4");
    }

    [Fact]
    public void Link_snr_is_not_capped_one_series_per_heard_partner()
    {
        // The deliberate scope decision: a per-callsign SNR label is emitted for EVERY heard partner
        // with a measured SNR (amateur-packet station counts are naturally small — no top-N cap).
        var log = new HeardLog(store: null);
        for (int i = 0; i < 50; i++)
        {
            var peer = new Callsign("G" + (char)('A' + i % 26) + "X", (byte)(i % 16));
            log.Record("vhf", peer.ToString(), T0.AddSeconds(i), snrDb: 10f + i);
        }

        var s = ParseSamples(RenderLinkSnr(log));
        s.Keys.Where(k => k.StartsWith("pdn_link_snr_db", StringComparison.Ordinal))
            .Should().HaveCount(50, "every heard partner with an SNR gets its own series — no cap");
    }

    [Fact]
    public void Link_snr_bucket_is_absent_when_no_heard_log_or_no_snr()
    {
        RenderLinkSnr(heard: null).Should().BeEmpty();

        var log = new HeardLog(store: null);
        log.Record("vhf", "G0ABC-1", T0, rssiDbm: -80f);   // heard, but no SNR ever measured
        RenderLinkSnr(log).Should().NotContain("pdn_link_snr_db");
    }

    // ─── per-partner pre-data carrier (TXDELAY as heard) ────────────────────────

    [Fact]
    public void Link_predata_emits_one_gauge_per_heard_partner_with_a_measured_median()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);
        log.Record("vhf", "GB7XXX", T0.AddMinutes(1), preDataCarrierMs: 420f);
        log.Record("vhf", "M0LTE-1", T0.AddSeconds(2), preDataCarrierMs: 150f);
        // Heard, but only an SNR reading (no pre-data) — must NOT appear in this gauge.
        log.Record("vhf", "M7NIL-3", T0.AddSeconds(3), snrDb: 12f);

        var body = RenderLinkPreData(log);
        var s = ParseSamples(body);

        body.Should().Contain("# TYPE pdn_link_predata_carrier_ms gauge");
        s["pdn_link_predata_carrier_ms{port=\"vhf\",peer=\"GB7XXX\"}"].Should().Be(410);
        s["pdn_link_predata_carrier_ms{port=\"vhf\",peer=\"M0LTE-1\"}"].Should().Be(150);
        s.Keys.Where(k => k.StartsWith("pdn_link_predata_carrier_ms", StringComparison.Ordinal))
            .Should().HaveCount(2, "the SNR-only partner is excluded");
        body.Should().NotContain("M7NIL-3");
    }

    [Fact]
    public void Link_predata_bucket_is_absent_when_no_heard_log_or_no_measurement()
    {
        RenderLinkPreData(heard: null).Should().BeEmpty();

        var log = new HeardLog(store: null);
        log.Record("vhf", "GB7XXX", T0, rssiDbm: -80f);   // heard, but no pre-data ever measured
        RenderLinkPreData(log).Should().NotContain("pdn_link_predata_carrier_ms");
    }

    // ─── the full document over a no-radio host ─────────────────────────────────

    [Fact]
    public void Render_over_a_radioless_host_emits_neither_radio_nor_link_snr_series()
    {
        var clock = new FakeTimeProvider(T0);
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = Local.ToString(), Alias = "LONDON" },
            Ports = [new PortConfig { Id = "vhf", Enabled = false, Transport = new KissTcpTransport { Host = "x", Port = 1 } }],
        });
        // Null supervisor + no heard log wired → no radios projected, no SNR source.
        var host = new NodeHostedService(config, null, clock, NullLoggerFactory.Instance);

        var body = PdnMetricsApi.Render(host, config, clock, traffic: null);
        body.Should().NotContain("pdn_radio_");
        body.Should().NotContain("pdn_link_snr");
    }
}
