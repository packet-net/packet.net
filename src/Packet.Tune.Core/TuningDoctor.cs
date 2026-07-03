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
}

/// <summary>
/// The capability doctor: probes a NinoTNC (and optionally the Tait CCDI
/// radio wired to it) for everything a tuning session needs, each probe
/// yielding pass/fail/unknown plus a one-line remedial action.
/// <b>The doctor transmits</b>: the TXDELAY check keys four short probe
/// frames, the SDM probe sends one wildcard SDM, and the pairing probe keys
/// one frame — run it on a bench/test channel.
/// </summary>
public static class TuningDoctor
{
    /// <summary>
    /// Run all probes. Opens (and closes) the given ports itself.
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

        // 1. TNC present (GETVER).
        string? firmware = null;
        try
        {
            firmware = await tnc.GetVersionAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            Add(new DoctorProbe("tnc-present", DoctorOutcome.Pass, $"GETVER answered: firmware {firmware}", null));
        }
        catch (TimeoutException)
        {
            Add(new DoctorProbe(
                "tnc-present", DoctorOutcome.Fail, "no GETVER reply",
                $"check a NinoTNC is on {tncPort} (57600 8N1) and powered"));
            return results; // nothing else is meaningful without a TNC
        }

        // 2. Firmware feature: GETRSSI (3.41-era; removed in 3.44). Probe with
        //    a short timeout rather than trusting version heuristics.
        try
        {
            float level = await tnc.GetRssiAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            Add(new DoctorProbe(
                "getrssi", DoctorOutcome.Pass,
                string.Create(CultureInfo.InvariantCulture, $"available ({level:0.0} dB) — firmware 3.41-era feature"),
                null));
        }
        catch (TimeoutException)
        {
            Add(new DoctorProbe(
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
            Add(new DoctorProbe("dip-software-control", DoctorOutcome.Unknown, "GETALL got no reply", null));
            Add(new DoctorProbe("running-mode", DoctorOutcome.Unknown, "GETALL got no reply", null));
        }
        else
        {
            Add(status.IsSoftwareControlMode switch
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
                Add(new DoctorProbe(
                    "running-mode", DoctorOutcome.Pass,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"mode {mode.Mode} ({mode.Name}){(ninetySixHundredClass ? " — note: high-rate modes are dead on narrow 12.5 kHz channels" : string.Empty)}"),
                    null));
            }
            else
            {
                Add(new DoctorProbe(
                    "running-mode", DoctorOutcome.Fail,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"mode byte 0x{status.FirmwareModeByte?.ToString("X2", CultureInfo.InvariantCulture) ?? "??"} unknown to the catalog"),
                    "SETHW a known-good mode (e.g. 6 = 1200 AFSK) — a fresh flash boots mode 0"));
            }
        }

        // 5. TXDELAY software control — with the mode pinned first (a stale
        //    post-flash mode 0 yields a false "pot override" verdict).
        try
        {
            var source = Callsign.Parse(opts.Callsign);
            await tnc.SetModeAsync(opts.PinMode, persistToFlash: false, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            int bitRate = NinoTncCatalog.TryGetByMode(opts.PinMode) is { BitRateHz: > 0 } entry ? entry.BitRateHz : 1200;
            var check = await TxDelayControlCheck.RunAsync(tnc, source, bitRate, log: null, cancellationToken)
                .ConfigureAwait(false);
            Add(new DoctorProbe(
                "txdelay-software-control",
                check.UnderSoftwareControl ? DoctorOutcome.Pass : DoctorOutcome.Fail,
                string.Create(CultureInfo.InvariantCulture, $"(mode pinned to {opts.PinMode} first) {check.Summary}"),
                check.UnderSoftwareControl ? null : "turn the TXDELAY pot fully anticlockwise"));
        }
        catch (TimeoutException ex)
        {
            Add(new DoctorProbe(
                "txdelay-software-control", DoctorOutcome.Unknown,
                $"probe transmission failed: {ex.Message}",
                "is the TNC able to key the radio? (check PTT wiring)"));
        }

        if (ccdiPort is null)
        {
            return results;
        }

        // 6. Radio present (CCDI identity).
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
            try
            {
                var identity = await radio.QueryIdentityAsync(cancellationToken).ConfigureAwait(false);
                Add(new DoctorProbe(
                    "radio-present", DoctorOutcome.Pass,
                    $"{identity.ProductName} s/n {identity.SerialNumber} (CCDI {identity.CcdiVersion})", null));
            }
            catch (TimeoutException)
            {
                Add(new DoctorProbe(
                    "radio-present", DoctorOutcome.Fail, "no CCDI response to a MODEL query",
                    $"check the radio is on {ccdiPort} at 28800 8N1 and in Command mode"));
                return results;
            }

            // 7. PROGRESS messages (carries DCD, PTT, SDM receipts).
            bool progressEnabled = false;
            try
            {
                await radio.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
                progressEnabled = true;
                Add(new DoctorProbe(
                    "progress-messages", DoctorOutcome.Pass, "enabled for this session (FUNCTION 0/4/1 accepted)", null));
            }
            catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
            {
                Add(new DoctorProbe(
                    "progress-messages", DoctorOutcome.Fail, $"could not enable: {ex.Message}",
                    "DCD/PTT/SDM-receipt events will not flow — check radio firmware/config"));
            }

            // 8. SDM enabled in the radio's programming (needed by the SDM
            //    tuning link). A wildcard send exercises the whole path; the
            //    radio rejects with error 0/06 when programming disables SDM.
            try
            {
                await radio.SendSdmAsync("********", "PDNDOCTR", leadInDelay: null, cancellationToken)
                    .ConfigureAwait(false);
                Add(new DoctorProbe(
                    "sdm", DoctorOutcome.Pass, "wildcard SDM accepted (one short over-air transmission)", null));
            }
            catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
            {
                Add(new DoctorProbe(
                    "sdm", DoctorOutcome.Fail, "radio rejected the SDM (error 0/06)",
                    "SDM is disabled in the radio's programming — enable SDM + auto-acks with the Tait programming app"));
            }
            catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
            {
                Add(new DoctorProbe("sdm", DoctorOutcome.Unknown, $"SDM send failed: {ex.Message}", null));
            }

            // 9. TNC↔radio pairing: key a short frame through the TNC and
            //    watch this radio report its transmitter keying.
            if (!progressEnabled)
            {
                Add(new DoctorProbe(
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
                        Add(new DoctorProbe(
                            "tnc-radio-pairing", DoctorOutcome.Pass,
                            "radio reported PTT within 2 s of the TNC keying a frame", null));
                    }
                    catch (TimeoutException)
                    {
                        Add(new DoctorProbe(
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

        return results;
    }

    /// <summary>Fixed-width token for an outcome (<c>PASS</c>/<c>FAIL</c>/<c>????</c>).</summary>
    public static string OutcomeToken(DoctorOutcome outcome) => outcome switch
    {
        DoctorOutcome.Pass => "PASS",
        DoctorOutcome.Fail => "FAIL",
        _ => "????",
    };
}
