using System.Text;
using Packet.Ax25;
using Packet.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Packet.Term;

/// <summary>
/// The Spectre.Console-based three-pane TUI: frame monitor on top,
/// chat in the middle, status bar, input line at the bottom. Owns the
/// foreground render loop and the keystroke read loop; delegates all
/// session lifecycle work to <see cref="SessionRunner"/>.
/// </summary>
public sealed class TerminalUi : IDisposable
{
    /// <inheritdoc/>
    public void Dispose() => runner.Dispose();

    // ─── Ring buffers — both panes scroll latest-at-bottom ────────────
    private readonly RingBuffer frameLog = new(200);
    private readonly RingBuffer chatLog = new(200);

    private readonly Callsign myCall;
    private readonly string portName;
    private readonly KissSerialModem modem;
    private readonly SessionRunner runner;

    private readonly StringBuilder inputBuffer = new();
    private readonly object inputLock = new();

    private LinkState linkState = LinkState.Disconnected;
    private Callsign? remote;
    private volatile bool quit;
    private volatile bool paused;     // suspended for AnsiConsole.Ask inside the live view

    /// <summary>Construct the UI. Caller is responsible for disposing the modem afterwards.</summary>
    public TerminalUi(Callsign myCall, string portName, KissSerialModem modem)
    {
        this.myCall = myCall;
        this.portName = portName;
        this.modem = modem ?? throw new ArgumentNullException(nameof(modem));

        runner = new SessionRunner(modem, myCall, line => AddChat(line), (s, peer) =>
        {
            linkState = s;
            remote = peer;
        });

        // Tap every outbound + inbound AX.25 frame into the monitor pane
        // through the listener's promiscuous-capture event. The listener
        // never filters this stream by addressing — Tom's brief
        // explicitly wants the monitor to see everything.
        runner.FrameTraced += (_, e) =>
            AddFrame(
                e.Direction == Packet.Ax25.Session.FrameDirection.Transmitted
                    ? FrameDirection.Transmit
                    : FrameDirection.Receive,
                e.Frame);
    }

    /// <summary>
    /// Start the inbound pump + render loop + key-read loop and block
    /// until the user quits.
    /// </summary>
    public async Task RunAsync(Callsign? autoConnect, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pumpTask = runner.Start(cts.Token);
        var keyTask = Task.Run(() => KeyLoop(cts.Token), cts.Token);

        // If --connect was provided, kick the initial outbound connection
        // without waiting for the user to press C.
        Task? autoConnectTask = null;
        if (autoConnect is { } target)
        {
            autoConnectTask = Task.Run(async () =>
            {
                // Brief settle so the render loop is up first.
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                AddChat($"*** Auto-connecting to {Display(target)}...");
                var outcome = await runner.ConnectAsync(target, TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
                if (outcome is null)
                {
                    AddChat("*** Connect timed out");
                }
                else if (outcome.Name == "DL_CONNECT_confirm")
                {
                    AddChat($"*** Connected to {Display(target)}");
                }
                else
                {
                    AddChat("*** Connect refused / link torn down before UA arrived");
                }
            }, cts.Token);
        }

        var live = AnsiConsole.Live(BuildLayout()).Overflow(VerticalOverflow.Visible).Cropping(VerticalOverflowCropping.Bottom);
        await live.StartAsync(async ctx =>
        {
            try
            {
                while (!cts.IsCancellationRequested && !quit)
                {
                    if (!paused)
                    {
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                    }
                    await Task.Delay(80, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }).ConfigureAwait(false);

        await cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(pumpTask, keyTask, autoConnectTask ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (AggregateException) { /* normal shutdown */ }
    }

    // ─── Layout building ──────────────────────────────────────────────

    private Grid BuildLayout()
    {
        // The Layout API is row-based with absolute / ratio sizing. Build it
        // fresh each tick: cheap, and avoids stale-state edge cases.
        var monitor = BuildPanel("frame monitor", frameLog.Snapshot(), Color.Cyan1, MonitorMarkup);
        var chat    = BuildPanel("chat",          chatLog.Snapshot(),  Color.Green, ChatMarkup);
        var status  = BuildStatusBar();
        var input   = BuildInputLine();

        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow(monitor);
        grid.AddRow(chat);
        grid.AddRow(status);
        grid.AddRow(input);
        return grid;
    }

    private static Panel BuildPanel(string title, IReadOnlyList<string> lines, Color titleColor, Func<string, string> markup)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(markup(line));
        }
        var content = sb.Length > 0
            ? new Markup(sb.ToString().TrimEnd())
            : (IRenderable)new Markup("[dim]" + Markup.Escape("(empty)") + "[/]");

        var panel = new Panel(content)
        {
            Header = new PanelHeader($"[{titleColor.ToMarkup()}]{title}[/]", Justify.Left),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
        return panel;
    }

    private string MonitorMarkup(string line)
    {
        // Color the direction marker — T (transmit) in yellow, R (receive)
        // in cyan. The rest of the line goes through Markup.Escape so
        // Spectre doesn't try to interpret payload bytes as markup.
        var escaped = Markup.Escape(line);
        if (line.Length > 9 && line[8] == ' ')
        {
            char dir = line[9];
            if (dir == 'T')
            {
                return $"[dim]{Markup.Escape(line[..8])}[/] [yellow1]T[/] {Markup.Escape(line[10..])}";
            }
            if (dir == 'R')
            {
                return $"[dim]{Markup.Escape(line[..8])}[/] [cyan1]R[/] {Markup.Escape(line[10..])}";
            }
        }
        return escaped;
    }

    private string ChatMarkup(string line)
    {
        // *** system events render in yellow; everything else is plain.
        var escaped = Markup.Escape(line);
        if (line.StartsWith("*** ", StringComparison.Ordinal))
        {
            return $"[yellow1]{escaped}[/]";
        }
        if (line.Length > 11 && line[0] == '[' && line[10] == ']')
        {
            // "[HH:MM:SS] ..."
            return $"[dim]{Markup.Escape(line[..11])}[/] {Markup.Escape(line[12..])}";
        }
        return escaped;
    }

    private Panel BuildStatusBar()
    {
        var stateColor = linkState switch
        {
            LinkState.Connected => "green1",
            LinkState.Connecting or LinkState.Disconnecting => "yellow1",
            _ => "red1",
        };
        var stateText = linkState switch
        {
            LinkState.Connected => $"connected to {Display(remote ?? new Callsign("UNK"))}",
            LinkState.Connecting => $"connecting to {Display(remote ?? new Callsign("UNK"))}",
            LinkState.Disconnecting => "disconnecting",
            _ => "disconnected",
        };

        var keys = linkState switch
        {
            LinkState.Connected => "[bold]D[/]isconnect  [bold]Q[/]uit",
            LinkState.Connecting or LinkState.Disconnecting => "[bold]Q[/]uit",
            _ => "[bold]C[/]onnect  [bold]S[/]ettings  [bold]Q[/]uit",
        };

        var bar = new Markup(
            $"MYCALL [bold]{Markup.Escape(Display(myCall))}[/]  ·  {Markup.Escape(portName)} @ 57600  ·  LINK: [{stateColor}]{stateText}[/]  ·  {keys}");

        return new Panel(bar)
        {
            Border = BoxBorder.None,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private Panel BuildInputLine()
    {
        string text;
        lock (inputLock)
        {
            text = inputBuffer.ToString();
        }

        IRenderable inner;
        if (linkState == LinkState.Connected)
        {
            inner = new Markup($"[bold]> [/]{Markup.Escape(text)}[blink]_[/]");
        }
        else
        {
            inner = new Markup("[dim]> (not connected — input disabled)[/]");
        }

        return new Panel(inner)
        {
            Border = BoxBorder.Rounded,
            Expand = true,
            Header = new PanelHeader("[grey]input[/]", Justify.Left),
        };
    }

    // ─── Key loop ─────────────────────────────────────────────────────

    private async Task KeyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !quit)
        {
            if (!Console.KeyAvailable)
            {
                try { await Task.Delay(40, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            ConsoleKeyInfo k;
            try { k = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return; }

            // Ctrl-C always quits cleanly, regardless of state.
            if (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0)
            {
                await QuitAsync(ct).ConfigureAwait(false);
                return;
            }

            if (linkState == LinkState.Connected)
            {
                await HandleKeyConnected(k, ct).ConfigureAwait(false);
            }
            else
            {
                await HandleKeyDisconnected(k, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleKeyDisconnected(ConsoleKeyInfo k, CancellationToken ct)
    {
        switch (k.Key)
        {
            case ConsoleKey.Q:
                await QuitAsync(ct).ConfigureAwait(false);
                return;
            case ConsoleKey.C when linkState == LinkState.Disconnected:
                await PromptAndConnect(ct).ConfigureAwait(false);
                return;
            case ConsoleKey.S when linkState == LinkState.Disconnected:
                await PromptSettings(ct).ConfigureAwait(false);
                return;
            default:
                // Disabled input — drop keystroke.
                return;
        }
    }

    private async Task HandleKeyConnected(ConsoleKeyInfo k, CancellationToken ct)
    {
        switch (k.Key)
        {
            case ConsoleKey.Q:
                await QuitAsync(ct).ConfigureAwait(false);
                return;
            case ConsoleKey.D when (k.Modifiers & ConsoleModifiers.Alt) == 0:
                // Note: pressing 'd' (no modifier) triggers disconnect; this
                // matches Tom's keymap. The trade-off is no literal 'd' can
                // be typed in chat — fine for a v1.
                await runner.DisconnectAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                return;
            case ConsoleKey.Enter:
                SubmitInputLine();
                return;
            case ConsoleKey.Backspace:
                lock (inputLock)
                {
                    if (inputBuffer.Length > 0) inputBuffer.Length--;
                }
                return;
        }

        // Printable characters go to the buffer.
        if (!char.IsControl(k.KeyChar) && k.KeyChar != '\0')
        {
            lock (inputLock)
            {
                inputBuffer.Append(k.KeyChar);
            }
        }
    }

    private void SubmitInputLine()
    {
        string toSend;
        lock (inputLock)
        {
            toSend = inputBuffer.ToString();
            inputBuffer.Clear();
        }
        if (toSend.Length == 0) return;

        AddChat($"me: {toSend}");
        var bytes = Encoding.ASCII.GetBytes(toSend + "\r");
        runner.SendData(bytes);
    }

    private async Task PromptAndConnect(CancellationToken ct)
    {
        paused = true;
        try
        {
            AnsiConsole.WriteLine();
            var input = AnsiConsole.Ask<string>("[bold]Connect to callsign[/]:", AppContext.LastConnectTarget ?? "");
            if (!Callsign.TryParse(input, out var target))
            {
                AnsiConsole.MarkupLine($"[red]Invalid callsign:[/] {Markup.Escape(input)}");
                await Task.Delay(800, ct).ConfigureAwait(false);
                return;
            }
            AppContext.LastConnectTarget = target.ToString();
            AppContext.SaveSettings();

            AddChat($"*** Connecting to {Display(target)}...");
        }
        finally
        {
            paused = false;
        }

        var outcome = await runner.ConnectAsync(target: Callsign.Parse(AppContext.LastConnectTarget!), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (outcome is null)
        {
            AddChat("*** Connect timed out");
        }
        else if (outcome.Name == "DL_CONNECT_confirm")
        {
            AddChat($"*** Connected to {AppContext.LastConnectTarget}");
        }
        else
        {
            AddChat("*** Connect refused / link torn down before UA arrived");
        }
    }

    private Task PromptSettings(CancellationToken ct)
    {
        paused = true;
        try
        {
            AnsiConsole.WriteLine();
            var newCall = AnsiConsole.Ask<string>("[bold]MYCALL[/]:", Display(myCall));
            if (Callsign.TryParse(newCall, out var parsed))
            {
                AppContext.Settings.MyCall = parsed.ToString();
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid callsign, leaving unchanged[/]");
            }

            var newPort = AnsiConsole.Ask<string>("[bold]Serial port[/]:", portName);
            if (!string.IsNullOrWhiteSpace(newPort))
            {
                AppContext.Settings.SerialPort = newPort;
            }
            AppContext.SaveSettings();
            AnsiConsole.MarkupLine("[dim]Settings saved. They take effect on the next run.[/]");
            return Task.Delay(1000, ct);
        }
        finally
        {
            paused = false;
        }
    }

    private async Task QuitAsync(CancellationToken ct)
    {
        if (linkState == LinkState.Connected)
        {
            AddChat("*** Quitting — disconnecting first...");
            await runner.DisconnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        quit = true;
    }

    // ─── Frame + chat sinks ───────────────────────────────────────────

    private void AddFrame(FrameDirection direction, ReadOnlyMemory<byte> bytes)
    {
        var line = FrameFormatter.Format(direction, bytes.Span, DateTimeOffset.Now);
        foreach (var sub in line.Split('\n'))
        {
            frameLog.Add(sub);
        }
    }

    private void AddFrame(FrameDirection direction, Packet.Ax25.Ax25Frame frame)
    {
        var line = FrameFormatter.Format(direction, frame, DateTimeOffset.Now);
        foreach (var sub in line.Split('\n'))
        {
            frameLog.Add(sub);
        }
    }

    private void AddChat(string line)
    {
        chatLog.Add($"[{DateTimeOffset.Now.LocalDateTime:HH:mm:ss}] {line}");
    }

    private static string Display(Callsign c) => c.Ssid == 0 ? c.Base : c.ToString();
}
