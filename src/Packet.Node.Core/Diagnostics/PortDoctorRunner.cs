using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Radio;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Diagnostics;

/// <summary>
/// Runs the <see cref="TuningDoctor"/> capability probes against a live port's <b>already-open</b>
/// handles and projects them into a <see cref="PortDoctorReport"/> for the node API. One
/// implementation backs both forms of <c>/api/v1/ports/{id}/doctor</c>:
/// <list type="bullet">
///   <item>the <b>safe</b> <c>GET</c> form (<paramref name="includeTransmitting"/> = false) runs
///     only the non-transmitting probes — identity, firmware, DIPs, running mode, radio identity,
///     GETRSSI availability — and reports the transmitting probes (TXDELAY software-control, the
///     SDM-enabled check, TNC↔radio pairing) as <c>unknown</c> "requires a brief transmit";</item>
///   <item>the <b>interrupt</b> <c>POST ?interrupt=true</c> form
///     (<paramref name="includeTransmitting"/> = true) additionally runs those transmitting probes,
///     which briefly key the transmitter and perturb TXDELAY.</item>
/// </list>
/// Runs are serialised node-wide (a single-flight gate) so two doctor requests never interleave
/// NinoTNC command traffic or transmit concurrently.
/// </summary>
public sealed class PortDoctorRunner : IDisposable
{
    /// <summary>The probe-execution seam. Defaults to
    /// <see cref="TuningDoctor.RunProbesAsync"/>; tests inject a stand-in to assert the
    /// safe-vs-interrupt gate without touching serial hardware.</summary>
    public delegate Task<IReadOnlyList<DoctorProbe>> ProbeRunner(
        NinoTncSerialPort? tnc, TaitCcdiRadio? radio, TuningDoctorOptions options, CancellationToken cancellationToken);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock;
    private readonly ProbeRunner run;

    /// <summary>Production constructor (system clock, real <see cref="TuningDoctor"/>).</summary>
    public PortDoctorRunner()
        : this(null, null)
    {
    }

    /// <summary>Test/override constructor.</summary>
    /// <param name="clock">Clock for <see cref="PortDoctorReport.RanAt"/>; null = system.</param>
    /// <param name="probeRunner">Probe-execution seam; null = the real
    /// <see cref="TuningDoctor.RunProbesAsync"/>.</param>
    public PortDoctorRunner(TimeProvider? clock, ProbeRunner? probeRunner)
    {
        this.clock = clock ?? TimeProvider.System;
        this.run = probeRunner
            ?? ((tnc, radio, options, ct) => TuningDoctor.RunProbesAsync(tnc, radio, options, onProbe: null, ct));
    }

    /// <summary>
    /// Probe a live port and project the result. The caller resolves the handles off the port's
    /// <c>RunningPort</c> (and has already 404'd an unknown/not-running port).
    /// </summary>
    /// <param name="portId">The port being probed (echoed into the report).</param>
    /// <param name="tnc">The port's NinoTNC handle, or <c>null</c> for a non-NinoTNC modem
    /// (the TNC-diagnostic probes then report "not a NinoTNC").</param>
    /// <param name="radio">The port's radio-control handle, or <c>null</c> when none is attached.</param>
    /// <param name="radioKind">The configured radio kind (for the "unsupported radio" note), or null.</param>
    /// <param name="includeTransmitting">When <c>true</c>, run the transmitting probes too
    /// (the interrupt form). When <c>false</c> (the safe default), nothing is transmitted.</param>
    /// <param name="callsign">Source callsign for the transmitting probes.</param>
    /// <param name="transportKind">The port's transport kind (e.g. <c>tait-transparent</c>) — a
    /// <c>tait-transparent</c> port gets the Transparent-readiness checklist instead of the
    /// TNC/CCDI probes (its radio is a byte pipe while running, not a NinoTNC or a command-mode
    /// CCDI radio). Null / any other kind ⇒ the standard probes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<PortDoctorReport> RunAsync(
        string portId,
        NinoTncSerialPort? tnc,
        IRadioControl? radio,
        string? radioKind,
        bool includeTransmitting,
        string callsign,
        string? transportKind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(portId);

        if (transportKind == TransportKinds.TaitTransparent)
        {
            return new PortDoctorReport(portId, TransparentReadinessChecklist(), clock.GetUtcNow());
        }

        var tait = radio as TaitCcdiRadio;
        var options = new TuningDoctorOptions
        {
            Callsign = string.IsNullOrWhiteSpace(callsign) ? "N0CALL" : callsign,
            IncludeTransmittingProbes = includeTransmitting,
        };

        IReadOnlyList<DoctorProbe> probes;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            probes = await run(tnc, tait, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var mapped = new List<PortDoctorProbe>(probes.Count + 1);
        foreach (var probe in probes)
        {
            mapped.Add(new PortDoctorProbe(probe.Name, StatusToken(probe.Outcome), probe.Detail, probe.Remedy));
        }

        // Node context the probe core can't infer: make the radio-attachment situation explicit
        // whenever there is no Tait radio for the deep radio probes to have run against.
        if (radio is null)
        {
            mapped.Add(new PortDoctorProbe(
                "radio-attached", "unknown", "no radio attached to this port", null));
        }
        else if (tait is null)
        {
            mapped.Add(new PortDoctorProbe(
                "radio-attached", "unknown",
                $"radio kind '{radioKind ?? "?"}' attached — the doctor's radio probes support tait-ccdi only",
                null));
        }

        return new PortDoctorReport(portId, mapped, clock.GetUtcNow());
    }

    /// <summary>
    /// The Transparent-readiness checklist for a running <c>tait-transparent</c> port. A running
    /// Transparent port's radio is a byte pipe — there is no CCDI control channel to run the
    /// behavioral enter/escape/baud-clean probes against without tearing the link down and risking
    /// a wedge, so the node reports what the running state proves and points the operator at the
    /// setup-time CLI (<c>packet-tune transparent-doctor</c>) for the disruptive behavioral checks.
    /// </summary>
    private static List<PortDoctorProbe> TransparentReadinessChecklist() =>
    [
        new PortDoctorProbe(
            TransparentReadinessDoctor.EnabledProbe, "pass",
            "Transparent mode active — this port is running as a byte pipe, so the radio accepted the Transparent-mode entry command (t) at startup",
            null),
        new PortDoctorProbe(
            TransparentReadinessDoctor.EscapeProbe, "unknown",
            "the +++ escape is exercised only at port teardown and cannot be safely re-tested on a live byte pipe. "
                + "Stop the port and run `packet-tune transparent-doctor <ccdiPort> --interrupt` to verify recovery",
            "if the port ever fails to return to Command mode when stopped, the radio has 'Ignore Escape Sequence' on — "
                + "uncheck it in the Data form (a wedged radio needs a power cycle)"),
        new PortDoctorProbe(
            TransparentReadinessDoctor.BaudCleanProbe, "unknown",
            "a byte-clean round-trip needs a controlled loopback with a peer radio. "
                + "Run `packet-tune transparent-doctor <ccdiPort> <peerCcdiPort> --interrupt` at setup",
            null),
    ];

    private static string StatusToken(DoctorOutcome outcome) => outcome switch
    {
        DoctorOutcome.Pass => "pass",
        DoctorOutcome.Fail => "fail",
        _ => "unknown",
    };

    /// <summary>Releases the single-flight gate (disposed by the DI container at shutdown).</summary>
    public void Dispose() => gate.Dispose();
}
