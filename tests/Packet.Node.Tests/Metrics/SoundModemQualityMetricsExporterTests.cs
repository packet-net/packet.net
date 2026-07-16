using Packet.Node.Api;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// The per-port in-process-soundmodem FEC receive-quality bucket (#635), driven directly over
/// synthetic <see cref="SoundModemQualitySnapshot"/> rows (the <see cref="HeadEndMetricsExporterTests"/>
/// pattern). Pins: absent-bucket-when-no-soundmodem-ports, counters-from-zero with the bounded
/// <c>port</c> label, and the last-frame gauge's null-vs-0 rule — omitted when the last frame carried
/// no FEC count (HDLC), emitted as 0 for a clean IL2P frame (the two are different facts).
/// </summary>
[Trait("Category", "Node")]
public sealed class SoundModemQualityMetricsExporterTests
{
    private static string Render(params (string PortId, SoundModemQualitySnapshot Snapshot)[] rows)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteSoundModemQuality(w, rows);
        return w.ToString();
    }

    private static SoundModemQualitySnapshot Snap(
        long frames, long correctedBytes, long correctedFrames, int? lastCorrected)
        => new(frames, correctedBytes, correctedFrames, lastCorrected, []);

    [Fact]
    public void No_soundmodem_ports_emits_nothing()
        => Render().Should().BeEmpty();

    [Fact]
    public void Counters_carry_the_port_label_and_emit_from_zero()
    {
        var body = Render(("sm0", Snap(frames: 10, correctedBytes: 42, correctedFrames: 3, lastCorrected: 2)));

        body.Should().Contain("# TYPE pdn_port_fec_corrected_bytes_total counter");
        body.Should().Contain("pdn_port_fec_frames_total{port=\"sm0\"} 10");
        body.Should().Contain("pdn_port_fec_corrected_bytes_total{port=\"sm0\"} 42");
        body.Should().Contain("pdn_port_fec_corrected_frames_total{port=\"sm0\"} 3");
        body.Should().Contain("pdn_port_fec_last_frame_corrected_bytes{port=\"sm0\"} 2");
    }

    [Fact]
    public void Clean_link_emits_zero_counters_and_a_zero_last_frame_gauge()
    {
        var body = Render(("sm0", Snap(frames: 5, correctedBytes: 0, correctedFrames: 0, lastCorrected: 0)));

        // Emitted from zero so rate() works from the first scrape.
        body.Should().Contain("pdn_port_fec_corrected_bytes_total{port=\"sm0\"} 0");
        // 0 (a clean FEC frame) is a real value — the gauge IS emitted, distinct from the null case.
        body.Should().Contain("pdn_port_fec_last_frame_corrected_bytes{port=\"sm0\"} 0");
    }

    [Fact]
    public void Hdlc_last_frame_null_omits_only_the_gauge_sample_not_the_counters()
    {
        var body = Render(("sm0", Snap(frames: 5, correctedBytes: 0, correctedFrames: 0, lastCorrected: null)));

        // Counters are still present (from zero) ...
        body.Should().Contain("pdn_port_fec_frames_total{port=\"sm0\"} 5");
        body.Should().Contain("pdn_port_fec_corrected_bytes_total{port=\"sm0\"} 0");
        // ... but the last-frame gauge is entirely absent — an unknown count is never a misleading 0.
        body.Should().NotContain("pdn_port_fec_last_frame_corrected_bytes");
    }

    [Fact]
    public void Each_port_gets_its_own_sample()
    {
        var body = Render(
            ("sm0", Snap(1, 0, 0, 0)),
            ("sm1", Snap(2, 5, 1, 5)));

        body.Should().Contain("pdn_port_fec_frames_total{port=\"sm0\"} 1");
        body.Should().Contain("pdn_port_fec_frames_total{port=\"sm1\"} 2");
        body.Should().Contain("pdn_port_fec_corrected_bytes_total{port=\"sm1\"} 5");
    }
}
