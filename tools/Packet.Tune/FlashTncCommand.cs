using System.Diagnostics;
using System.Globalization;
using Packet.Kiss.NinoTnc;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Tune;

/// <summary>
/// <c>flash-tnc</c>: flash a NinoTNC with an Intel-HEX firmware image using
/// the native C# bootloader flasher
/// (<see cref="BootloaderNinoTncFirmwareFlasher"/>). Pre-flight: classifies
/// the image's target chip, refuses if another process holds the port
/// (Linux), and reads the running firmware version over KISS GETVER. Asks
/// for confirmation unless <c>--yes</c>. After a successful flash, waits for
/// the reboot (+ first-boot bootloader self-update) and re-verifies with
/// GETVER — reminding the operator that the RAM mode has reset to 0.
/// </summary>
internal static class FlashTncCommand
{
    public static async Task<int> Run(string tncPort, string hexPath, string[] rest)
    {
        bool yes = rest.Contains("--yes");

        byte[] hexBytes;
        try
        {
            hexBytes = await File.ReadAllBytesAsync(hexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.WriteLine($"flash-tnc: cannot read hex file: {ex.Message}");
            return 2;
        }

        NinoTncFirmwareHexImage image;
        try
        {
            image = NinoTncFirmwareHexImage.Parse(hexBytes);
        }
        catch (NinoTncFlashException ex)
        {
            Console.WriteLine($"flash-tnc: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"flash-tnc — TNC {tncPort}");
        Console.WriteLine($"  image: {Path.GetFileName(hexPath)} ({hexBytes.Length} bytes, {image.Lines.Count} lines)");
        Console.WriteLine($"  image target chip: {Describe(image.TargetChip)}");

        // Refuse to race another process for the port (KISS host, another
        // flasher, a terminal). A flash through a shared port strands the TNC.
        var holders = FindPortHolders(tncPort);
        if (holders.Count > 0)
        {
            Console.WriteLine($"  REFUSED: {tncPort} is held by: {string.Join(", ", holders)}");
            Console.WriteLine("  Close the other process(es) and re-run.");
            return 1;
        }

        // Best-effort running-firmware read. A stranded bootloader (or a TNC
        // mid-boot) won't answer — that's not fatal, the flasher probes for it.
        string? runningVersion = null;
        try
        {
            await using var tnc = NinoTncSerialPort.Open(tncPort);
            runningVersion = await tnc.GetVersionAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // no GETVER reply — possibly a stranded bootloader
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.WriteLine($"  cannot open {tncPort}: {ex.Message}");
            return 1;
        }
        Console.WriteLine(runningVersion is not null
            ? $"  running firmware (GETVER): {runningVersion}"
            : "  running firmware: no GETVER reply — TNC may be in a stranded bootloader (the flasher will probe and resume)");

        Console.WriteLine();
        Console.WriteLine("  The transfer takes 2-4 minutes. DO NOT interrupt it, unplug the TNC, or let the");
        Console.WriteLine("  machine sleep: an interrupted flash strands the TNC in its bootloader (recoverable");
        Console.WriteLine("  by re-running this command, but the TNC is dead until then). After a successful");
        Console.WriteLine("  flash the TNC reboots and its RAM mode resets to 0 — re-apply your mode via set-mode.");
        if (!yes)
        {
            Console.Write($"  Proceed with flashing {tncPort}? [y/N] ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  aborted — nothing written.");
                return 1;
            }
        }

        var flasher = new BootloaderNinoTncFirmwareFlasher();
        var stopwatch = Stopwatch.StartNew();
        NinoTncFlashResult result;
        try
        {
            result = await flasher.FlashAsync(tncPort, hexBytes, new ConsoleLineProgress());
            Console.WriteLine();
        }
        catch (NinoTncFlashException ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  FLASH FAILED ({ex.Failure}): {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  flash successful: {result.LinesWritten}/{result.TotalLines} lines in " +
                          $"{result.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s " +
                          $"(bootloader '{result.BootloaderVersion}', {Describe(result.Chip)}" +
                          (result.ResumedStrandedBootloader ? ", resumed a stranded bootloader" : string.Empty) + ")");

        // The TNC is rebooting; the first boot after a flash also runs a
        // bootloader self-update (~2 s). Give it a moment, then verify.
        Console.WriteLine("  waiting for the TNC to reboot (first boot runs a ~2 s bootloader self-update)...");
        await Task.Delay(TimeSpan.FromSeconds(4));
        var verifyDeadline = stopwatch.Elapsed + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                await using var tnc = NinoTncSerialPort.Open(tncPort);
                string version = await tnc.GetVersionAsync(TimeSpan.FromSeconds(3));
                Console.WriteLine($"  post-flash GETVER: firmware {version}");
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (stopwatch.Elapsed > verifyDeadline)
                {
                    Console.WriteLine($"  post-flash GETVER did not answer within 20 s ({ex.GetType().Name}) — " +
                                      "power-cycle the TNC and check it manually.");
                    return 1;
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        Console.WriteLine("  reminder: the RAM mode reset to 0 — restore your mode (e.g. `set-mode " + tncPort + " 6`).");
        return 0;
    }

    private static string Describe(NinoTncChipVariant chip) => chip switch
    {
        NinoTncChipVariant.Dspic33Ep256 => "dsPIC33EP256GP (firmware 3.xx)",
        NinoTncChipVariant.Dspic33Ep512 => "dsPIC33EP512GP (firmware 4.xx)",
        _ => "unknown",
    };

    /// <summary>
    /// Find processes holding <paramref name="portName"/> open, lsof-style,
    /// by scanning <c>/proc/*/fd</c> (Linux only; other platforms return
    /// empty — SerialPort's own open will still fail if the port is locked).
    /// Only processes owned by the current user are visible without
    /// privileges, which covers the realistic collision (our own KISS host).
    /// </summary>
    private static List<string> FindPortHolders(string portName)
    {
        var holders = new List<string>();
        if (!OperatingSystem.IsLinux())
        {
            return holders;
        }

        string target;
        try
        {
            target = Path.GetFullPath(new FileInfo(portName).LinkTarget ?? portName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            target = portName;
        }

        int self = Environment.ProcessId;
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == self)
            {
                continue;
            }
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(procDir, "fd")))
                {
                    string? link = new FileInfo(fd).LinkTarget;
                    if (link is not null && string.Equals(link, target, StringComparison.Ordinal))
                    {
                        string comm = File.ReadAllText(Path.Combine(procDir, "comm")).Trim();
                        holders.Add($"{comm} (pid {pid})");
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // process exited, or not ours to inspect — skip
            }
        }
        return holders;
    }

    /// <summary>Single-line console progress, updated when the percentage
    /// changes (the flasher reports every line — ~17 k reports).</summary>
    private sealed class ConsoleLineProgress : IProgress<NinoTncFlashProgress>
    {
        private int lastPercent = -1;

        public void Report(NinoTncFlashProgress value)
        {
            int percent = value.Percent;
            if (Interlocked.Exchange(ref lastPercent, percent) == percent)
            {
                return;
            }
            Console.Write($"\r  flashing: {value.LinesWritten}/{value.TotalLines} lines ({percent}%)   ");
        }
    }
}
