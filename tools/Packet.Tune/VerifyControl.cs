using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>verify-control</c>: prove a NinoTNC is under software control.
/// Reads the DIP positions + config mode from GETALL, pins a known-good
/// mode (SETHW, non-persist — default 6, <c>--mode N</c> overrides,
/// <c>--keep-mode</c> skips), then measures the effective TXDELAY at two
/// commanded values via <see cref="TxDelayControlCheck"/>. If the measured
/// value tracks the commanded one, the TXDELAY pot is at minimum and KISS
/// TXDELAY is in charge; if it doesn't, the pot is overriding.
/// </summary>
/// <remarks>
/// The mode pinning exists because of a real bench failure (2026-07-02):
/// straight after the 3.44 firmware flash the TNC boots mode 0 (9600 GFSK —
/// dead on the rig's narrow channels), and the timing check then produced a
/// FALSE "pot override" verdict. Measure in a mode that actually decodes.
/// </remarks>
internal static class VerifyControl
{
    public static async Task<int> Run(string tncPort, string[] rest)
    {
        string callsign = "N0CALL";
        byte pinMode = 6;
        bool keepMode = false;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--mode" when i + 1 < rest.Length &&
                                   byte.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m):
                    pinMode = m;
                    i++;
                    break;
                case "--keep-mode":
                    keepMode = true;
                    break;
                default:
                    callsign = rest[i];
                    break;
            }
        }

        Console.WriteLine($"verify-control — TNC {tncPort}, test frames from {callsign}");
        var source = Callsign.Parse(callsign);

        await using var tnc = NinoTncSerialPort.Open(tncPort);

        var status = await tnc.GetAllAsync();
        Console.WriteLine($"  firmware: {status.FirmwareVersionRaw ?? "?"}");
        Console.WriteLine($"  DIP positions (reg 04): {status.DipSwitchesBinary ?? "?"}" +
                          (status.IsSoftwareControlMode switch
                          {
                              true => "  → software control (1111)",
                              false => "  → HARDWARE mode pinned by DIPs",
                              null => "  (register missing)",
                          }));
        Console.WriteLine($"  config mode (reg 06): 0x{status.FirmwareModeByte?.ToString("X2", CultureInfo.InvariantCulture) ?? "??"}" +
                          $" → {status.RunningMode?.Name ?? "unknown to catalog"}");

        int bitRate;
        if (keepMode)
        {
            bitRate = status.RunningMode is { BitRateHz: > 0 } mode ? mode.BitRateHz : 1200;
            if (status.RunningMode is not { BitRateHz: > 0 })
            {
                Console.WriteLine($"  ! mode bit rate unknown — assuming {bitRate} bit/s for the preamble arithmetic");
            }
        }
        else
        {
            // Pin a known-good mode before measuring: a stale mode (e.g. the
            // post-flash default 0 — 9600 GFSK, dead on narrow channels)
            // makes the timing check report a false "pot override".
            Console.WriteLine($"  pinning mode {pinMode} (SETHW +16, non-persist) for the check — --keep-mode skips this");
            await tnc.SetModeAsync(pinMode, persistToFlash: false);
            await Task.Delay(250);
            bitRate = NinoTncCatalog.TryGetByMode(pinMode) is { BitRateHz: > 0 } pinned ? pinned.BitRateHz : 1200;
        }
        Console.WriteLine();

        var result = await TxDelayControlCheck.RunAsync(
            tnc, source, bitRate, log: line => Console.WriteLine("  " + line));

        Console.WriteLine();
        if (!result.UsedRegisterPath)
        {
            Console.WriteLine("  (register 0B did not move — firmware 3.41/3.44 behaviour; using ACKMODE echo timing)");
        }
        Console.WriteLine($"verdict: {result.Summary}");
        return result.UnderSoftwareControl ? 0 : 1;
    }
}
