using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// The remote deviation-tuning commands: <c>deviation-sdm</c> (coordination
/// over Tait SDMs — radio-native FFSK at factory deviation, independent of
/// the pot under tune) and <c>deviation-remote</c> (coordination over a
/// WebSocket rendezvous relay with a spoken 6-digit PIN). Both run the same
/// <see cref="TuningSession"/> assistant loop; only the
/// <see cref="ITuningLink"/> differs.
/// </summary>
internal static class DeviationAssist
{
    private sealed record CommonArgs(
        string Role, string Tnc, string? Radio, string Callsign, int BurstFrames, bool Verbose);

    public static async Task<int> RunSdm(string[] args)
    {
        var (common, flags) = Parse(args);
        if (common is null || !flags.TryGetValue("--peer", out string? peer))
        {
            Console.WriteLine("deviation-sdm needs --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId>");
            return 2;
        }
        if (common.Radio is null)
        {
            Console.WriteLine("deviation-sdm needs --radio <ccdi> — the SDM link rides the radio's own modem");
            return 2;
        }

        Console.WriteLine($"deviation-sdm — role {common.Role}, TNC {common.Tnc}, radio {common.Radio}, peer {peer}");
        using var radio = TaitCcdiRadio.Open(common.Radio);
        // PROGRESS messages carry DCD, PTT, SDM arrivals and delivery receipts.
        await radio.SetProgressMessagesAsync(true);

        await using var link = SdmTuningLink.Create(radio, peer);
        if (common.Verbose)
        {
            link.Log = line => Console.WriteLine("  " + line);
        }
        return await RunSession(common, link, common.Role == TuningSession.MeterRole ? radio : null);
    }

    public static async Task<int> RunRemote(string[] args)
    {
        var (common, flags) = Parse(args);
        if (common is null || !flags.TryGetValue("--rendezvous", out string? rendezvous))
        {
            Console.WriteLine("deviation-remote needs --role tuned|meter --tnc <port> --rendezvous <ws url> [--pin N]");
            return 2;
        }

        string? pin = flags.GetValueOrDefault("--pin");
        if (pin is null)
        {
            if (common.Role == TuningSession.TunedRole)
            {
                pin = RendezvousRelay.GeneratePin();
                Console.WriteLine($"session PIN: {Spaced(pin)} — read this to the meter operator");
            }
            else
            {
                Console.Write("enter the session PIN the tuned operator read out: ");
                pin = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(pin))
                {
                    Console.WriteLine("no PIN — aborting");
                    return 2;
                }
            }
        }

        Console.WriteLine($"deviation-remote — role {common.Role}, TNC {common.Tnc}" +
                          (common.Radio is null ? string.Empty : $", radio {common.Radio}") +
                          $", rendezvous {rendezvous}");

        TaitCcdiRadio? radio = null;
        if (common.Radio is not null && common.Role == TuningSession.MeterRole)
        {
            radio = TaitCcdiRadio.Open(common.Radio);
            await radio.SetProgressMessagesAsync(true);
        }
        else if (common.Radio is not null)
        {
            Console.WriteLine("  (note: the tuned role does not use --radio — coordination is over the relay)");
        }

        using (radio)
        {
            await using var link = await WebSocketTuningLink.ConnectAsync(new Uri(rendezvous), pin, common.Role);
            Console.WriteLine("  joined the rendezvous session — waiting for the other end if not there yet");
            return await RunSession(common, link, radio);
        }
    }

    private static async Task<int> RunSession(CommonArgs common, ITuningLink link, TaitCcdiRadio? meterRadio)
    {
        var source = Callsign.Parse(common.Callsign);
        var options = new TuningSessionOptions { BurstFrames = common.BurstFrames };
        await using var tnc = NinoTncSerialPort.Open(common.Tnc);
        Console.WriteLine($"  TNC firmware: {await tnc.GetVersionAsync()}");
        Console.WriteLine("  tip: run both TNCs in mode 7 (1200 AFSK IL2P+CRC) so FEC-corrected-byte");
        Console.WriteLine("  deltas are meaningful — in plain AX.25 modes the meter works from decode");
        Console.WriteLine("  rate + clipping alone");

        using var cancelOnCtrlC = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancelOnCtrlC.Cancel();
        };

        try
        {
            if (common.Role == TuningSession.TunedRole)
            {
                // Arm this TNC's CQBEEP responder for the session (volatile —
                // a TNC reset disarms it; re-run the command after one). The
                // meter can then also trigger tone tests as a refinement.
                Console.WriteLine("  arming this TNC's CQBEEP responder ([TARPNstat) for the session...");
                await tnc.ArmCqBeepResponderAsync(source, cancelOnCtrlC.Token);
                await Task.Delay(1500, cancelOnCtrlC.Token); // let the arming frame finish keying

                var stimulus = new NinoTncBurstStimulus(tnc, source);
                var prompt = new ConsolePrompt();
                Console.WriteLine();
                return await TuningSession.RunTunedAsync(link, stimulus, prompt, options, Console.Out, cancelOnCtrlC.Token);
            }
            else
            {
                var meter = new NinoTncBurstMeter(tnc, meterRadio);
                if (common.Verbose)
                {
                    meter.Log = line => Console.WriteLine("  " + line);
                }
                Console.WriteLine();
                return await TuningSession.RunMeterAsync(link, meter, options, Console.Out, cancelOnCtrlC.Token);
            }
        }
        catch (TuningLinkException ex)
        {
            Console.WriteLine($"tuning link failed: {ex.Message}");
            Console.WriteLine("(SDM flavour: is the peer radio on and its SDM identity right? " +
                              "internet flavour: is the relay reachable and the PIN correct?)");
            return 1;
        }
    }

    private static (CommonArgs? Common, Dictionary<string, string> Flags) Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--verbose")
            {
                verbose = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                flags[args[i]] = args[++i];
            }
        }

        string? role = flags.GetValueOrDefault("--role");
        string? tnc = flags.GetValueOrDefault("--tnc");
        if (role is not (TuningSession.TunedRole or TuningSession.MeterRole) || tnc is null)
        {
            return (null, flags);
        }
        int burst = int.TryParse(flags.GetValueOrDefault("--burst"), NumberStyles.None, CultureInfo.InvariantCulture, out int b) && b is > 0 and <= 50
            ? b
            : 5;
        return (new CommonArgs(role, tnc, flags.GetValueOrDefault("--radio"),
            flags.GetValueOrDefault("--callsign") ?? "N0CALL", burst, verbose), flags);
    }

    private static string Spaced(string pin) => string.Join('-', pin.ToCharArray());

    /// <summary>Operator prompt on the console: Enter = re-run, q = finish.</summary>
    private sealed class ConsolePrompt : ITuningPrompt
    {
        public Task<bool> ContinueAsync(CancellationToken cancellationToken = default)
        {
            Console.Write("adjust the TX-DEV pot, then [Enter] to re-run — or q[Enter] to finish: ");
            string? input = Console.ReadLine();
            return Task.FromResult(
                input is not null && !input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase));
        }
    }
}
