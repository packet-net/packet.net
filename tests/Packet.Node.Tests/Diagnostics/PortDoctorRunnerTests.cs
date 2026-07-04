using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Diagnostics;
using Packet.Node.Tests.Support;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Diagnostics;

/// <summary>
/// The node-side capability-doctor projection (<see cref="PortDoctorRunner"/>): the
/// <see cref="PortDoctorReport"/> shape, the safe-vs-interrupt gate (via an injected probe seam,
/// so no serial hardware is touched), and the serial-KISS / no-radio degradation. The 404 for an
/// unknown port lives at the endpoint (<c>PortDoctorApiTests</c>).
/// </summary>
[Trait("Category", "Node")]
public sealed class PortDoctorRunnerTests
{
    /// <summary>A runner whose probe core is replaced by a stand-in that records the options it was
    /// called with and returns <paramref name="canned"/> — lets us assert the gate deterministically.</summary>
    private static PortDoctorRunner WithSeam(List<TuningDoctorOptions> calls, IReadOnlyList<DoctorProbe>? canned = null)
    {
        var probes = canned ?? [new DoctorProbe("tnc-present", DoctorOutcome.Pass, "ok", null)];
        return new PortDoctorRunner(TimeProvider.System, (tnc, radio, options, ct) =>
        {
            calls.Add(options);
            return Task.FromResult(probes);
        });
    }

    [Fact]
    public async Task Safe_form_runs_the_probe_core_with_transmitting_disabled()
    {
        var calls = new List<TuningDoctorOptions>();
        var runner = WithSeam(calls);

        await runner.RunAsync("p1", tnc: null, radio: null, radioKind: null, includeTransmitting: false, callsign: "N0CALL");

        calls.Should().ContainSingle();
        calls[0].IncludeTransmittingProbes.Should().BeFalse();
    }

    [Fact]
    public async Task Interrupt_form_runs_the_probe_core_with_transmitting_enabled_and_the_callsign()
    {
        var calls = new List<TuningDoctorOptions>();
        var runner = WithSeam(calls);

        await runner.RunAsync("p1", null, radio: null, radioKind: null, includeTransmitting: true, callsign: "M0LTE-1");

        calls[0].IncludeTransmittingProbes.Should().BeTrue();
        calls[0].Callsign.Should().Be("M0LTE-1");
    }

    [Fact]
    public async Task Projects_probe_outcomes_to_lowercase_status_tokens_and_echoes_ids()
    {
        var runner = WithSeam(new List<TuningDoctorOptions>(), canned:
        [
            new DoctorProbe("tnc-present", DoctorOutcome.Pass, "GETVER answered", null),
            new DoctorProbe("dip-software-control", DoctorOutcome.Fail, "pinned by switches", "set DIPs to 1111"),
            new DoctorProbe("getrssi", DoctorOutcome.Unknown, "no reply", null),
        ]);

        var report = await runner.RunAsync("vhf", null, null, null, includeTransmitting: false, "N0CALL");

        report.PortId.Should().Be("vhf");
        report.RanAt.Should().NotBe(default);
        report.Probes.Should().HaveCountGreaterThanOrEqualTo(3);
        report.Probes[0].Should().BeEquivalentTo(new { Name = "tnc-present", Status = "pass" });
        report.Probes[1].Should().BeEquivalentTo(new { Name = "dip-software-control", Status = "fail", Remedy = "set DIPs to 1111" });
        report.Probes[2].Should().BeEquivalentTo(new { Name = "getrssi", Status = "unknown" });
    }

    [Fact]
    public async Task No_radio_appends_a_radio_attached_unknown_row()
    {
        var runner = WithSeam(new List<TuningDoctorOptions>());

        var report = await runner.RunAsync("p1", null, radio: null, radioKind: null, includeTransmitting: false, "N0CALL");

        var row = report.Probes.Should().ContainSingle(p => p.Name == "radio-attached").Subject;
        row.Status.Should().Be("unknown");
        row.Detail.Should().Contain("no radio attached");
    }

    [Fact]
    public async Task A_non_tait_radio_reports_the_deep_radio_probes_are_unsupported()
    {
        await using var radio = new FakeRadioControl();
        var runner = WithSeam(new List<TuningDoctorOptions>());

        var report = await runner.RunAsync("p1", null, radio, radioKind: "some-cat", includeTransmitting: false, "N0CALL");

        var row = report.Probes.Should().ContainSingle(p => p.Name == "radio-attached").Subject;
        row.Status.Should().Be("unknown");
        row.Detail.Should().Contain("some-cat").And.Contain("tait-ccdi");
    }

    [Fact]
    public async Task Real_probe_core_degrades_a_serial_kiss_no_radio_port_without_touching_hardware()
    {
        // No seam ⇒ the real TuningDoctor.RunProbesAsync runs; null handles do zero serial I/O.
        using var runner = new PortDoctorRunner();

        var report = await runner.RunAsync("hf", tnc: null, radio: null, radioKind: null, includeTransmitting: false, "N0CALL");

        report.Probes.Should().Contain(p =>
            p.Name == "tnc-present" && p.Status == "unknown" && p.Detail.Contains("not a NinoTNC"));
        report.Probes.Should().Contain(p => p.Name == "radio-attached" && p.Status == "unknown");
        report.Probes.Should().NotContain(p => p.Name == "radio-present");
    }
}
