namespace Packet.Tune.Core.Tests;

/// <summary>
/// The refactored capability-doctor probe core (<see cref="TuningDoctor.RunProbesAsync"/>) that
/// runs against already-open handles — the seam the PDN node reuses. The null-handle paths do
/// zero serial I/O, so these exercise the degradation + gating structure without hardware (the
/// live-handle probe bodies are covered by the hardware-loop suite).
/// </summary>
public class TuningDoctorTests
{
    private static readonly string[] TncProbeNames =
        ["tnc-present", "getrssi", "dip-software-control", "running-mode", "txdelay-software-control"];

    [Fact]
    public async Task RunProbesAsync_with_no_nino_reports_the_tnc_probes_as_not_a_ninotnc()
    {
        var probes = await TuningDoctor.RunProbesAsync(tnc: null, radio: null);

        probes.Select(p => p.Name).Should().Equal(TncProbeNames);
        probes.Should().OnlyContain(p => p.Outcome == DoctorOutcome.Unknown);
        probes.Should().OnlyContain(p => p.Detail.Contains("not a NinoTNC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunProbesAsync_with_no_radio_emits_no_radio_probes()
    {
        var probes = await TuningDoctor.RunProbesAsync(tnc: null, radio: null);

        string[] radioProbeNames = ["radio-present", "progress-messages", "sdm", "tnc-radio-pairing"];
        probes.Select(p => p.Name).Should().NotContain(radioProbeNames);
    }

    [Fact]
    public async Task RunProbesAsync_with_no_nino_never_transmits_regardless_of_the_gate()
    {
        var safe = await TuningDoctor.RunProbesAsync(
            null, null, new TuningDoctorOptions { IncludeTransmittingProbes = false });
        var full = await TuningDoctor.RunProbesAsync(
            null, null, new TuningDoctorOptions { IncludeTransmittingProbes = true });

        // No NinoTNC ⇒ nothing to transmit through; both forms degrade identically (no throw, no I/O).
        safe.Select(p => (p.Name, p.Outcome)).Should().Equal(full.Select(p => (p.Name, p.Outcome)));
    }

    [Fact]
    public async Task RunProbesAsync_invokes_the_onProbe_sink_once_per_probe_in_order()
    {
        var seen = new List<DoctorProbe>();
        var probes = await TuningDoctor.RunProbesAsync(null, null, onProbe: seen.Add);

        seen.Should().Equal(probes);
    }

    [Fact]
    public void OutcomeToken_renders_fixed_width_tokens()
    {
        TuningDoctor.OutcomeToken(DoctorOutcome.Pass).Should().Be("PASS");
        TuningDoctor.OutcomeToken(DoctorOutcome.Fail).Should().Be("FAIL");
        TuningDoctor.OutcomeToken(DoctorOutcome.Unknown).Should().Be("????");
    }
}
