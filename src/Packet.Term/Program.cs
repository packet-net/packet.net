using System.IO.Ports;
using CommandLine;
using Packet.Core;
using Packet.Term.Tui;

namespace Packet.Term;

/// <summary>
/// Entry point. Parses CLI, resolves MYCALL + serial port (CLI override
/// → settings → interactive prompt), opens the modem, hands off to the
/// Terminal.Gui v2 app shell in <see cref="PacketTermApp"/>.
/// </summary>
/// <remarks>
/// All boot-time prompts happen here via plain
/// <see cref="Console.ReadLine"/> — before the TUI takes over the screen.
/// Once the modem is open, control transfers to <see cref="PacketTermApp.Run"/>,
/// which owns the entire screen until the user exits.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        var parsed = Parser.Default.ParseArguments<CommandLineOptions>(args);
        if (parsed is not CommandLine.Parsed<CommandLineOptions> ok)
        {
            return 1;
        }
        var opts = ok.Value;

        AppContext.Load();

        // When MYCALL and port are both supplied via CLI, treat this run
        // as ephemeral — disable settings persistence so two parallel
        // instances started with their own --mycall and --port don't race
        // on the shared settings.json. The Connect-target history and
        // any Settings-dialog changes during this run won't survive to
        // disk, which is the right trade-off when the CLI is the source
        // of truth for identity + port.
        if (!string.IsNullOrWhiteSpace(opts.MyCall) && !string.IsNullOrWhiteSpace(opts.Port))
        {
            AppContext.PersistenceEnabled = false;
            Console.WriteLine("Packet.Term: --mycall + --port both provided, settings persistence disabled for this run.");
        }

        // Resolve MYCALL: --mycall > settings > prompt.
        var myCallStr = opts.MyCall ?? AppContext.Settings.MyCall;
        if (string.IsNullOrWhiteSpace(myCallStr))
        {
            Console.Write("MYCALL (your callsign + SSID, e.g. M0LTE-1): ");
            myCallStr = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(myCallStr))
            {
                Console.Error.WriteLine("MYCALL is required.");
                return 2;
            }
            AppContext.Settings.MyCall = myCallStr;
            AppContext.SaveSettings();
        }
        if (!Callsign.TryParse(myCallStr, out var myCall))
        {
            Console.Error.WriteLine($"Invalid MYCALL: {myCallStr}");
            return 2;
        }

        // Resolve port: --port > settings > selection prompt.
        var portName = opts.Port ?? AppContext.Settings.SerialPort;
        if (string.IsNullOrWhiteSpace(portName))
        {
            portName = ChoosePort();
            if (portName is null)
            {
                Console.Error.WriteLine("No serial ports found. Re-run with --port /path/to/port once a modem is attached.");
                return 3;
            }
            AppContext.Settings.SerialPort = portName;
            AppContext.SaveSettings();
        }

        // --connect: validate up-front and bail out fast on a typo, before
        // we open the modem.
        Callsign? autoConnect = null;
        if (!string.IsNullOrWhiteSpace(opts.Connect))
        {
            if (!Callsign.TryParse(opts.Connect, out var ac))
            {
                Console.Error.WriteLine($"Invalid --connect callsign: {opts.Connect}");
                return 4;
            }
            autoConnect = ac;
        }

        KissSerialModem modem;
        try
        {
            modem = KissSerialModem.Open(portName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open {portName}: {ex.Message}");
            return 5;
        }

        Console.WriteLine($"Packet.Term {AppInfo.Version}  MYCALL={myCall}  port={portName} @ 57600");
        Console.WriteLine("Starting TUI...");

        try
        {
            // MainWindow takes ownership of the modem now — it may swap
            // to a different port at runtime via the Settings dialog.
            // Don't wrap with `using` here; MainWindow.Dispose handles it.
            PacketTermApp.Run(myCall, portName, modem, autoConnect);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Packet.Term aborted: {ex.Message}");
            return 6;
        }

        Console.WriteLine("Goodbye.");
        return 0;
    }

    private static string? ChoosePort()
    {
        var ports = SerialPort.GetPortNames();
        if (ports.Length == 0) return null;
        Array.Sort(ports, StringComparer.Ordinal);

        Console.WriteLine("Available serial ports:");
        for (int i = 0; i < ports.Length; i++)
        {
            Console.WriteLine($"  [{i + 1}] {ports[i]}");
        }
        Console.Write($"Pick one [1-{ports.Length}], or type a path: ");
        var raw = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= ports.Length)
        {
            return ports[idx - 1];
        }
        return raw;
    }
}
