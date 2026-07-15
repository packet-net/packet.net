using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>A probe's verdict.</summary>
public enum DoctorOutcome
{
    /// <summary>The capability is present and working.</summary>
    Pass,

    /// <summary>The capability is absent/broken; see the remedy.</summary>
    Fail,

    /// <summary>Could not be determined (or is an informational finding,
    /// e.g. "GETRSSI removed on this firmware").</summary>
    Unknown,
}

/// <summary>One capability probe's result.</summary>
/// <param name="Name">Short probe identifier (e.g. <c>tnc-present</c>).</param>
/// <param name="Outcome">Pass / Fail / Unknown.</param>
/// <param name="Detail">What was observed.</param>
/// <param name="Remedy">One-line remedial action (null when none applies).</param>
public sealed record DoctorProbe(string Name, DoctorOutcome Outcome, string Detail, string? Remedy);

/// <summary>Options for <see cref="TuningDoctor"/>.</summary>
public sealed record TuningDoctorOptions
{
    /// <summary>Source callsign for probe transmissions. Default <c>N0CALL</c>.</summary>
    public string Callsign { get; init; } = "N0CALL";

    /// <summary>The known-good mode pinned (SETHW, non-persist) before the
    /// TXDELAY check — a stale post-flash mode 0 produces a false "pot
    /// override" verdict otherwise. Default 6 (1200 AFSK AX.25).</summary>
    public byte PinMode { get; init; } = 6;

    /// <summary>
    /// When <c>true</c> (the default, and what the <c>packet-tune doctor</c> CLI
    /// uses) the <b>transmitting</b> probes run: the TXDELAY software-control check,
    /// the wildcard-SDM check, and the TNC↔radio pairing check each briefly KEY THE
    /// TRANSMITTER (and the TXDELAY check perturbs the TXDELAY register). When
    /// <c>false</c> — the safe default when probing a <em>live</em> node port — those
    /// probes are reported <see cref="DoctorOutcome.Unknown"/> with a
    /// "requires a brief transmit" detail and <b>nothing is transmitted</b>.
    /// </summary>
    public bool IncludeTransmittingProbes { get; init; } = true;
}

/// <summary>
/// The capability doctor: probes a NinoTNC (and optionally the Tait CCDI
/// radio wired to it) for everything a tuning session needs, each probe
/// yielding pass/fail/unknown plus a one-line remedial action.
/// <b>The doctor can transmit</b>: when
/// <see cref="TuningDoctorOptions.IncludeTransmittingProbes"/> is on the TXDELAY check
/// keys four short probe frames, the SDM probe sends one wildcard SDM, and the pairing
/// probe keys one frame — run those on a bench/test channel.
/// </summary>
/// <remarks>
/// Two entry points share <b>one</b> probe implementation:
/// <list type="bullet">
///   <item><see cref="RunAsync(string,string,TuningDoctorOptions,Action{string},CancellationToken)"/>
///     opens (and closes) the serial ports itself — the standalone <c>packet-tune doctor</c> CLI.</item>
///   <item><see cref="RunProbesAsync(NinoTncSerialPort,TaitCcdiRadio,TuningDoctorOptions,Action{DoctorProbe},CancellationToken)"/>
///     runs against <b>already-open</b> handles — the PDN node passes its live
///     <c>RunningPort</c> handles so an operator can run the doctor without tearing the
///     port down.</item>
/// </list>
/// </remarks>
public static class TuningDoctor
{
    /// <summary>Detail shown for a transmitting probe skipped because
    /// <see cref="TuningDoctorOptions.IncludeTransmittingProbes"/> is off.</summary>
    private const string TransmitGatedDetail = "requires a brief transmit — rerun with interrupt=true";

    /// <summary>
    /// Run all probes, opening (and closing) the given serial ports itself.
    /// </summary>
    /// <param name="tncPort">The NinoTNC's serial port (e.g. <c>/dev/ttyACM0</c>).</param>
    /// <param name="ccdiPort">The radio's CCDI serial port, or null to skip radio probes.</param>
    /// <param name="options">Options; null = defaults.</param>
    /// <param name="progress">Optional live progress sink (one line per probe).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<IReadOnlyList<DoctorProbe>> RunAsync(
        string tncPort,
        string? ccdiPort,
        TuningDoctorOptions? options = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tncPort);
        var opts = options ?? new TuningDoctorOptions();
        var results = new List<DoctorProbe>();
        void Add(DoctorProbe probe)
        {
            results.Add(probe);
            progress?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"  [{OutcomeToken(probe.Outcome)}] {probe.Name}: {probe.Detail}"));
        }

        await using var tnc = NinoTncSerialPort.Open(tncPort);

        bool tncPresent = await RunTncProbesAsync(tnc, opts, Add, cancellationToken).ConfigureAwait(false);
        if (!tncPresent || ccdiPort is null)
        {
            return results; // nothing else is meaningful without a TNC / a radio port
        }

        // The radio-open failure is a CLI-only concern (the node hands us an already-open
        // radio), so it stays here rather than in the shared probe core.
        TaitCcdiRadio radio;
        try
        {
            radio = TaitCcdiRadio.Open(ccdiPort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Add(new DoctorProbe(
                "radio-present", DoctorOutcome.Fail, $"could not open {ccdiPort}: {ex.Message}",
                "check the CCDI serial port path and permissions"));
            return results;
        }

        using (radio)
        {
            await RunRadioProbesAsync(tnc, radio, opts, Add, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Run all probes against <b>already-open</b> handles, without opening or closing any
    /// serial port. This is the seam the PDN node uses: it passes the live
    /// <c>RunningPort</c> handles so the doctor probes the port in place.
    /// </summary>
    /// <param name="tnc">The open NinoTNC serial port, or <c>null</c> when the port's modem
    /// is not a NinoTNC (a serial-KISS modem exposes no NinoTNC diagnostics) — the
    /// TNC-diagnostic probes then report <see cref="DoctorOutcome.Unknown"/>
    /// "not a NinoTNC".</param>
    /// <param name="radio">The open Tait CCDI radio, or <c>null</c> to skip the radio probes.</param>
    /// <param name="options">Options; null = defaults. Set
    /// <see cref="TuningDoctorOptions.IncludeTransmittingProbes"/> to <c>false</c> to keep the
    /// run transmit-free (the safe default for a live port).</param>
    /// <param name="onProbe">Optional live sink invoked once per probe as it completes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<IReadOnlyList<DoctorProbe>> RunProbesAsync(
        NinoTncSerialPort? tnc,
        TaitCcdiRadio? radio,
        TuningDoctorOptions? options = null,
        Action<DoctorProbe>? onProbe = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new TuningDoctorOptions();
        var results = new List<DoctorProbe>();
        void Add(DoctorProbe probe)
        {
            results.Add(probe);
            onProbe?.Invoke(probe);
        }

        bool proceed = await RunTncProbesAsync(tnc, opts, Add, cancellationToken).ConfigureAwait(false);
        if (proceed && radio is not null)
        {
            await RunRadioProbesAsync(tnc, radio, opts, Add, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>The TNC-diagnostic probes. Returns <c>true</c> when the caller may proceed to
    /// the radio probes (a NinoTNC answered, or there is no NinoTNC at all so the radio may
    /// still be worth probing); <c>false</c> when a NinoTNC is present but dead (stop).</summary>
    private static async Task<bool> RunTncProbesAsync(
        NinoTncSerialPort? tnc,
        TuningDoctorOptions opts,
        Action<DoctorProbe> add,
        CancellationToken cancellationToken)
    {
        if (tnc is null)
        {
            // A serial-KISS (non-NinoTNC) modem. No NinoTNC diagnostics exist; emit each
            // TNC-diagnostic row as Unknown so the checklist stays consistent across port
            // kinds, then let any attached radio still be probed.
            const string notNino = "not a NinoTNC — this modem exposes no NinoTNC diagnostics";
            add(new DoctorProbe("tnc-present", DoctorOutcome.Unknown, notNino, null));
            add(new DoctorProbe("getrssi", DoctorOutcome.Unknown, notNino, null));
            add(new DoctorProbe("dip-software-control", DoctorOutcome.Unknown, notNino, null));
            add(new DoctorProbe("running-mode", DoctorOutcome.Unknown, notNino, null));
            add(new DoctorProbe("txdelay-software-control", DoctorOutcome.Unknown, notNino, null));
            return true;
        }

        // 1. TNC present (GETVER).
        try
        {
            string firmware = await tnc.GetVersionAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            add(new DoctorProbe("tnc-present", DoctorOutcome.Pass, $"GETVER answered: firmware {firmware}", null));
        }
        catch (TimeoutException)
        {
            add(new DoctorProbe(
                "tnc-present", DoctorOutcome.Fail, "no GETVER reply",
                $"check a NinoTNC is on {tnc.PortName} (57600 8N1) and powered"));
            return false; // nothing else is meaningful without a TNC
        }

        // 2. Firmware feature: GETRSSI (3.41-era; removed in 3.44). Probe with a short
        //    timeout rather than trusting version heuristics.
        try
        {
            float level = await tnc.GetRssiAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            add(new DoctorProbe(
                "getrssi", DoctorOutcome.Pass,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"available (firmware 3.41-era) — deviation meter fast path active (idle {level:0.0} dB)"),
                null));
        }
        catch (TimeoutException)
        {
            add(new DoctorProbe(
                "getrssi", DoctorOutcome.Unknown,
                "no reply in 2 s — removed in firmware 3.44 (was an undocumented 3.41 feature)",
                "meter deviation by decode-rate / FEC deltas instead (deviation-sdm / deviation-remote)"));
        }

        // 3+4. GETALL: DIP positions + running mode.
        NinoTncStatusFrame? status = null;
        try
        {
            status = await tnc.GetAllAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        if (status is null)
        {
            add(new DoctorProbe("dip-software-control", DoctorOutcome.Unknown, "GETALL got no reply", null));
            add(new DoctorProbe("running-mode", DoctorOutcome.Unknown, "GETALL got no reply", null));
        }
        else
        {
            add(status.IsSoftwareControlMode switch
            {
                true => new DoctorProbe(
                    "dip-software-control", DoctorOutcome.Pass, $"DIPs {status.DipSwitchesBinary} — software control", null),
                false => new DoctorProbe(
                    "dip-software-control", DoctorOutcome.Fail,
                    $"DIPs {status.DipSwitchesBinary} — mode pinned by switches",
                    "set all four DIP switches up (1111) so KISS SETHW controls the mode"),
                null => new DoctorProbe(
                    "dip-software-control", DoctorOutcome.Unknown, "DIP register missing from GETALL", null),
            });

            if (status.RunningMode is { } mode)
            {
                bool ninetySixHundredClass = mode.BitRateHz >= 4800;
                add(new DoctorProbe(
                    "running-mode", DoctorOutcome.Pass,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"mode {mode.Mode} ({mode.Name}){(ninetySixHundredClass ? " — note: high-rate modes are dead on narrow 12.5 kHz channels" : string.Empty)}"),
                    null));
            }
            else
            {
                add(new DoctorProbe(
                    "running-mode", DoctorOutcome.Fail,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"mode byte 0x{status.FirmwareModeByte?.ToString("X2", CultureInfo.InvariantCulture) ?? "??"} unknown to the catalog"),
                    "SETHW a known-good mode (e.g. 6 = 1200 AFSK) — a fresh flash boots mode 0"));
            }
        }

        // 5. TXDELAY software control — TRANSMITS (keys four probe frames + perturbs the
        //    TXDELAY register). Gated behind IncludeTransmittingProbes.
        if (!opts.IncludeTransmittingProbes)
        {
            add(new DoctorProbe("txdelay-software-control", DoctorOutcome.Unknown, TransmitGatedDetail, null));
        }
        else
        {
            try
            {
                var source = Callsign.Parse(opts.Callsign);
                await tnc.SetModeAsync(opts.PinMode, persistToFlash: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                int bitRate = NinoTncCatalog.TryGetByMode(opts.PinMode) is { BitRateHz: > 0 } entry ? entry.BitRateHz : 1200;
                var check = await TxDelayControlCheck.RunAsync(tnc, source, bitRate, log: null, cancellationToken)
                    .ConfigureAwait(false);
                add(new DoctorProbe(
                    "txdelay-software-control",
                    check.UnderSoftwareControl ? DoctorOutcome.Pass : DoctorOutcome.Fail,
                    string.Create(CultureInfo.InvariantCulture, $"(mode pinned to {opts.PinMode} first) {check.Summary}"),
                    check.UnderSoftwareControl ? null : "turn the TXDELAY pot fully anticlockwise"));
            }
            catch (TimeoutException ex)
            {
                add(new DoctorProbe(
                    "txdelay-software-control", DoctorOutcome.Unknown,
                    $"probe transmission failed: {ex.Message}",
                    "is the TNC able to key the radio? (check PTT wiring)"));
            }
        }

        return true;
    }

    /// <summary>The radio-diagnostic probes, against an already-open Tait CCDI radio.</summary>
    private static async Task RunRadioProbesAsync(
        NinoTncSerialPort? tnc,
        TaitCcdiRadio radio,
        TuningDoctorOptions opts,
        Action<DoctorProbe> add,
        CancellationToken cancellationToken)
    {
        // 6. Radio present (CCDI identity).
        try
        {
            var identity = await radio.QueryIdentityAsync(cancellationToken).ConfigureAwait(false);
            add(new DoctorProbe(
                "radio-present", DoctorOutcome.Pass,
                $"{identity.ProductName} s/n {identity.SerialNumber} (CCDI {identity.CcdiVersion})", null));
        }
        catch (TimeoutException)
        {
            add(new DoctorProbe(
                "radio-present", DoctorOutcome.Fail, "no CCDI response to a MODEL query",
                $"check the radio is on {radio.PortName} at 28800 8N1 and in Command mode"));
            return;
        }

        // 7. PROGRESS messages (carries DCD, PTT, SDM receipts). A config write, not a transmit.
        bool progressEnabled = false;
        try
        {
            await radio.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            progressEnabled = true;
            add(new DoctorProbe(
                "progress-messages", DoctorOutcome.Pass, "enabled for this session (FUNCTION 0/4/1 accepted)", null));
        }
        catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
        {
            add(new DoctorProbe(
                "progress-messages", DoctorOutcome.Fail, $"could not enable: {ex.Message}",
                "DCD/PTT/SDM-receipt events will not flow — check radio firmware/config"));
        }

        // 8. SDM enabled in the radio's programming. A wildcard send TRANSMITS (one short
        //    over-air burst); the radio rejects with error 0/06 when programming disables SDM.
        //    Gated behind IncludeTransmittingProbes — there is no non-transmitting probe for it.
        if (!opts.IncludeTransmittingProbes)
        {
            add(new DoctorProbe(
                "sdm", DoctorOutcome.Unknown,
                "SDM-enabled check " + TransmitGatedDetail, null));
        }
        else
        {
            try
            {
                await radio.SendSdmAsync("********", "PDNDOCTR", leadInDelay: null, cancellationToken)
                    .ConfigureAwait(false);
                add(new DoctorProbe(
                    "sdm", DoctorOutcome.Pass, "wildcard SDM accepted (one short over-air transmission)", null));
            }
            catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
            {
                add(new DoctorProbe(
                    "sdm", DoctorOutcome.Fail, "radio rejected the SDM (error 0/06)",
                    "SDM is disabled in the radio's programming — enable SDM + auto-acks with the Tait programming app"));
            }
            catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
            {
                add(new DoctorProbe("sdm", DoctorOutcome.Unknown, $"SDM send failed: {ex.Message}", null));
            }
        }

        // 9. TNC↔radio pairing: key a short frame through the TNC and watch this radio
        //    report its transmitter keying. TRANSMITS — gated behind IncludeTransmittingProbes.
        if (!opts.IncludeTransmittingProbes)
        {
            add(new DoctorProbe("tnc-radio-pairing", DoctorOutcome.Unknown, TransmitGatedDetail, null));
        }
        else if (tnc is null)
        {
            add(new DoctorProbe(
                "tnc-radio-pairing", DoctorOutcome.Unknown,
                "skipped — no NinoTNC to key a frame through", null));
        }
        else if (!progressEnabled)
        {
            add(new DoctorProbe(
                "tnc-radio-pairing", DoctorOutcome.Unknown,
                "skipped — PROGRESS messages unavailable (the PTT report rides on them)", null));
        }
        else
        {
            var ptt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTx(object? sender, TransmitterStateChange change)
            {
                if (change.Transmitting)
                {
                    ptt.TrySetResult();
                }
            }
            radio.TransmitterStateChanged += OnTx;
            try
            {
                var source = Callsign.Parse(opts.Callsign);
                await tnc.SendFrameAsync(TuningBurst.BuildFrame(source, 1, 1).ToBytes(), cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await ptt.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    add(new DoctorProbe(
                        "tnc-radio-pairing", DoctorOutcome.Pass,
                        "radio reported PTT within 2 s of the TNC keying a frame", null));
                }
                catch (TimeoutException)
                {
                    add(new DoctorProbe(
                        "tnc-radio-pairing", DoctorOutcome.Fail,
                        "radio saw no PTT within 2 s of the TNC transmitting",
                        "this TNC and radio may not be wired together — check the port↔radio mapping and PTT line"));
                }
            }
            finally
            {
                radio.TransmitterStateChanged -= OnTx;
            }
        }
    }

    /// <summary>Fixed-width token for an outcome (<c>PASS</c>/<c>FAIL</c>/<c>????</c>).</summary>
    public static string OutcomeToken(DoctorOutcome outcome) => outcome switch
    {
        DoctorOutcome.Pass => "PASS",
        DoctorOutcome.Fail => "FAIL",
        _ => "????",
    };
}
