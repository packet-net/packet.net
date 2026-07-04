using System.Globalization;
using System.Text.Json;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>transparent-doctor</c>: the readiness checks for a TNC-less Tait FFSK <b>Transparent</b>
/// link (the <c>tait-transparent</c> port). Behavioral pass/fail/unknown with a remedy naming the
/// exact Data-form field, for the codeplug settings that are NOT CCDI-readable:
/// <list type="number">
///   <item><c>transparent-mode-enabled</c> — the radio accepts Transparent-mode entry;</item>
///   <item><c>escape-recovers</c> — the <c>+++</c> escape returns it to Command mode (the
///     "Ignore Escape Sequence" check — <b>can wedge a misconfigured radio</b>);</item>
///   <item><c>baud-clean</c> — a known frame round-trips byte-for-byte through both radios' FFSK
///     (needs a peer CCDI port).</item>
/// </list>
/// The enter/escape and loopback probes are DISRUPTIVE (they enter Transparent mode and transmit)
/// and are only run with <c>--interrupt</c>. Without it they are reported <c>unknown</c> and
/// nothing enters Transparent or transmits. Exit code 0 = no failed probe, 1 = at least one failure.
/// </summary>
internal static class TransparentDoctorCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> Run(string ccdiPort, string[] rest)
    {
        string? peerPort = null;
        bool json = false;
        bool interrupt = false;
        string callsign = "N0CALL";
        int baud = TaitCcdiRadio.DefaultBaudRate;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--interrupt":
                    interrupt = true;
                    break;
                case "--callsign" when i + 1 < rest.Length:
                    callsign = rest[++i];
                    break;
                case "--baud" when i + 1 < rest.Length &&
                                   int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var b):
                    baud = b;
                    i++;
                    break;
                default:
                    peerPort = rest[i];
                    break;
            }
        }

        void Log(string line)
        {
            if (!json)
            {
                Console.WriteLine(line);
            }
        }

        if (!json)
        {
            Console.WriteLine(
                $"transparent-doctor — radio CCDI {ccdiPort}" +
                (peerPort is null ? string.Empty : $", peer CCDI {peerPort}"));
            Console.WriteLine(interrupt
                ? "(--interrupt: the enter/escape and loopback probes ENTER Transparent mode and TRANSMIT;"
                    + " a radio with 'Ignore Escape Sequence' ON may require a POWER CYCLE)"
                : "(safe: no probe enters Transparent or transmits — rerun with --interrupt for the behavioral checks)");
            Console.WriteLine();
        }

        var opts = new TransparentReadinessOptions { Callsign = callsign };
        var probes = new List<DoctorProbe>();
        void Add(DoctorProbe p)
        {
            probes.Add(p);
            Log($"  [{TuningDoctor.OutcomeToken(p.Outcome)}] {p.Name}: {p.Detail}");
            if (p.Remedy is not null)
            {
                Log($"          → {p.Remedy}");
            }
        }

        await using var radio = TaitCcdiRadio.Open(
            ccdiPort, baud, new TaitCcdiRadioOptions { KeepAliveInterval = null });

        // Radio present + in Command mode?
        try
        {
            var identity = await radio.QueryIdentityAsync();
            Add(new DoctorProbe(
                "radio-present", DoctorOutcome.Pass,
                $"{identity.ProductName} s/n {identity.SerialNumber} (CCDI {identity.CcdiVersion})", null));
        }
        catch (TimeoutException)
        {
            Add(new DoctorProbe(
                "radio-present", DoctorOutcome.Fail, "no CCDI response to a MODEL query",
                $"check the radio is on {ccdiPort} at {baud} 8N1 and in Command mode"));
            return Emit(probes, json);
        }

        if (!interrupt)
        {
            const string gated = "requires --interrupt (enters Transparent mode; a misconfigured radio may need a power cycle)";
            Add(new DoctorProbe(TransparentReadinessDoctor.EnabledProbe, DoctorOutcome.Unknown, gated, null));
            Add(new DoctorProbe(TransparentReadinessDoctor.EscapeProbe, DoctorOutcome.Unknown, gated, null));
            Add(new DoctorProbe(TransparentReadinessDoctor.BaudCleanProbe, DoctorOutcome.Unknown,
                "requires --interrupt and a peer CCDI port (transmits a loopback frame)", null));
            return Emit(probes, json);
        }

        // Probes 1 + 2: enter Transparent + verify the escape recovers. Leaves the radio in Command
        // mode when the escape works, or wedged in Transparent when it does not.
        var result = await TransparentReadinessDoctor.RunEnableAndEscapeProbesAsync(radio, opts, Add);

        if (result.RadioWedged)
        {
            Log(string.Empty);
            Log($"!!! WARNING: {ccdiPort} is WEDGED in Transparent mode — POWER-CYCLE the radio to recover. !!!");
            Add(new DoctorProbe(TransparentReadinessDoctor.BaudCleanProbe, DoctorOutcome.Unknown,
                "skipped — the local radio is wedged in Transparent mode", null));
            return Emit(probes, json, wedged: true);
        }

        // Probe 3: baud-clean, when a peer radio is available and the local radio recovered cleanly.
        if (peerPort is null || !result.EnteredAndRecovered)
        {
            Add(peerPort is null
                ? TransparentReadinessDoctor.BaudCleanNeedsPeer()
                : new DoctorProbe(TransparentReadinessDoctor.BaudCleanProbe, DoctorOutcome.Unknown,
                    "skipped — the local radio did not cleanly return to Command mode", null));
        }
        else
        {
            await RunBaudCleanAsync(radio, peerPort, baud, opts, Add, Log);
        }

        // Leave both radios verified in Command mode.
        await VerifyCommandModeAsync(radio, ccdiPort, Log);
        return Emit(probes, json);
    }

    private static async Task RunBaudCleanAsync(
        TaitCcdiRadio localRadio, string peerPort, int baud,
        TransparentReadinessOptions opts, Action<DoctorProbe> add, Action<string> log)
    {
        var transportOptions = new TaitTransparentTransportOptions { CommandBaud = baud, TransparentBaud = baud };
        TaitCcdiRadio? peerRadio = null;
        try
        {
            peerRadio = TaitCcdiRadio.Open(peerPort, baud, new TaitCcdiRadioOptions { KeepAliveInterval = null });

            // The transports own the enter/exit dance; ownsRadio:false so we keep the radios to
            // verify Command mode afterwards.
            await using var local = new TaitTransparentTransport(localRadio, transportOptions, ownsRadio: false);
            await using var peer = new TaitTransparentTransport(peerRadio, transportOptions, ownsRadio: false);
            try
            {
                await local.EnterTransparentModeAsync();
                await peer.EnterTransparentModeAsync();
            }
            catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
            {
                add(new DoctorProbe(TransparentReadinessDoctor.BaudCleanProbe, DoctorOutcome.Unknown,
                    "the peer radio rejected Transparent-mode entry (error 0/06 — its Transparent mode is disabled)",
                    TransparentReadinessDoctor.EnabledRemedy));
                return;
            }

            log("  looping a known frame local → peer over the FFSK byte pipe...");
            var probe = await TransparentReadinessDoctor.RunBaudCleanProbeAsync(local, peer, opts);
            add(probe);
            // `await using` on the two transports escapes Transparent and restores Command mode.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            add(new DoctorProbe(TransparentReadinessDoctor.BaudCleanProbe, DoctorOutcome.Unknown,
                $"could not open the peer radio on {peerPort}: {ex.Message}", null));
        }
        finally
        {
            if (peerRadio is not null)
            {
                await VerifyCommandModeAsync(peerRadio, peerPort, log);
                await peerRadio.DisposeAsync();
            }
        }
    }

    private static async Task VerifyCommandModeAsync(TaitCcdiRadio radio, string port, Action<string> log)
    {
        try
        {
            var identity = await radio.QueryIdentityAsync();
            log($"  verified {port} back in Command mode (MODEL: {identity.ProductName} s/n {identity.SerialNumber}).");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            log($"  WARNING: could not confirm {port} is in Command mode ({ex.Message}) — check the radio.");
        }
    }

    private static int Emit(List<DoctorProbe> probes, bool json, bool wedged = false)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    RadioWedged = wedged,
                    Probes = probes.Select(r => new
                    {
                        r.Name,
                        Outcome = r.Outcome.ToString().ToLowerInvariant(),
                        r.Detail,
                        r.Remedy,
                    }),
                },
                JsonOptions));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"{"probe",-26} {"outcome",-8} detail");
            foreach (var r in probes)
            {
                Console.WriteLine($"{r.Name,-26} {TuningDoctor.OutcomeToken(r.Outcome),-8} {r.Detail}");
                if (r.Remedy is not null)
                {
                    Console.WriteLine($"{string.Empty,-26} {string.Empty,-8} → {r.Remedy}");
                }
            }
        }

        return probes.Any(r => r.Outcome == DoctorOutcome.Fail) ? 1 : 0;
    }
}
