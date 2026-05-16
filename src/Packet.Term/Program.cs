using System.IO.Ports;
using CommandLine;
using Packet.Core;
using Spectre.Console;

namespace Packet.Term;

/// <summary>
/// Entry point. Parses CLI, resolves MYCALL + serial port (CLI override
/// → settings → interactive prompt), opens the modem, hands off to
/// <see cref="TerminalUi"/>.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = Parser.Default.ParseArguments<CommandLineOptions>(args);
        if (parsed is not CommandLine.Parsed<CommandLineOptions> ok)
        {
            return 1;
        }
        var opts = ok.Value;

        AppContext.Load();

        // Resolve MYCALL: --mycall > settings > prompt.
        var myCallStr = opts.MyCall ?? AppContext.Settings.MyCall;
        if (string.IsNullOrWhiteSpace(myCallStr))
        {
            myCallStr = AnsiConsole.Ask<string>("[bold]MYCALL[/] (your callsign + SSID, e.g. M0LTE-1):");
            AppContext.Settings.MyCall = myCallStr;
            AppContext.SaveSettings();
        }
        if (!Callsign.TryParse(myCallStr, out var myCall))
        {
            AnsiConsole.MarkupLine($"[red]Invalid MYCALL[/]: {Markup.Escape(myCallStr ?? string.Empty)}");
            return 2;
        }

        // Resolve port: --port > settings > selection prompt.
        var portName = opts.Port ?? AppContext.Settings.SerialPort;
        if (string.IsNullOrWhiteSpace(portName))
        {
            portName = ChoosePort();
            if (portName is null)
            {
                AnsiConsole.MarkupLine("[red]No serial ports found.[/] Re-run with --port /path/to/port once a modem is attached.");
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
                AnsiConsole.MarkupLine($"[red]Invalid --connect callsign[/]: {Markup.Escape(opts.Connect)}");
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
            AnsiConsole.MarkupLine($"[red]Failed to open[/] {Markup.Escape(portName)}: {Markup.Escape(ex.Message)}");
            return 5;
        }

        AnsiConsole.MarkupLine($"[green]Packet.Term {AppInfo.Version}[/]  MYCALL=[bold]{Markup.Escape(myCall.ToString())}[/]  port=[bold]{Markup.Escape(portName)}[/] @ 57600");
        AnsiConsole.MarkupLine("[dim]Starting TUI; press Q to quit.[/]");

        await using (modem)
        {
            var ui = new TerminalUi(myCall, portName, modem);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            await ui.RunAsync(autoConnect, cts.Token).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
        return 0;
    }

    private static string? ChoosePort()
    {
        var ports = SerialPort.GetPortNames();
        if (ports.Length == 0) return null;
        Array.Sort(ports, StringComparer.Ordinal);

        var prompt = new SelectionPrompt<string>()
            .Title("[bold]Pick a serial port[/]:")
            .AddChoices(ports)
            .PageSize(10);
        return AnsiConsole.Prompt(prompt);
    }
}
