using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>set-mode</c>: SETHW a NinoTNC operating mode (RAM-only +16 by default;
/// <c>--persist</c> writes flash), transmit the settle throwaway frame (the
/// NinoTNC applies a changed setting from the SECOND frame), and verify the
/// running mode via GETALL.
/// </summary>
internal static class SetModeCommand
{
    public static async Task<int> Run(string tncPort, string modeText, string[] rest)
    {
        if (!byte.TryParse(modeText, NumberStyles.None, CultureInfo.InvariantCulture, out byte mode) ||
            mode > NinoTncSetHardware.MaxMode)
        {
            Console.WriteLine($"set-mode: mode must be 0–{NinoTncSetHardware.MaxMode}");
            return 2;
        }
        bool persist = rest.Contains("--persist");
        string callsign = "N0CALL";
        for (int i = 0; i < rest.Length - 1; i++)
        {
            if (rest[i] == "--callsign")
            {
                callsign = rest[i + 1];
            }
        }

        var catalogued = NinoTncCatalog.TryGetByMode(mode);
        Console.WriteLine($"set-mode — TNC {tncPort} → mode {mode}" +
                          (catalogued is { } m ? $" ({m.Name})" : string.Empty) +
                          (persist ? " [PERSISTED TO FLASH]" : " [RAM only, +16]"));

        await using var tnc = NinoTncSerialPort.Open(tncPort);
        await tnc.SetModeAsync(mode, persist);

        byte[] settle = ModeSurvey.BuildSettleFrame(Callsign.Parse(callsign), mode).ToBytes();
        try
        {
            await tnc.SendFrameWithAckAsync(settle, TimeSpan.FromSeconds(15));
            Console.WriteLine("  settle frame transmitted (mode applies from the SECOND frame after SETHW)");
        }
        catch (TimeoutException)
        {
            Console.WriteLine("  settle frame TX-completion not echoed — the next real frame may still be in the old mode");
        }

        try
        {
            var status = await tnc.GetAllAsync(TimeSpan.FromSeconds(3));
            string running = status.RunningMode is { } rm
                ? $"{rm.Mode} ({rm.Name})"
                : $"unrecognised firmware byte 0x{status.FirmwareModeByte:X2}";
            Console.WriteLine($"  GETALL running mode: {running}");
            return status.RunningMode?.Mode == mode ? 0 : 1;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("  GETALL verify timed out — mode sent but unverified");
            return 1;
        }
    }
}
