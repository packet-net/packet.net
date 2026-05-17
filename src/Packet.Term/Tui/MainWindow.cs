using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Packet.Term.Tui;

/// <summary>
/// The Terminal.Gui v2 main window — a full-screen Turbo-Vision-style
/// shell with a top <see cref="MenuBar"/>, three stacked sub-views
/// (frame monitor → conversation → input), and a bottom
/// <see cref="StatusBar"/>. Owns the <see cref="SessionRunner"/> for the
/// lifetime of the TUI.
/// </summary>
/// <remarks>
/// All cross-thread updates from <see cref="SessionRunner"/> callbacks
/// (chat lines, link-state, frame trace) must marshal back onto the
/// Terminal.Gui main thread before touching any view. The
/// <see cref="IApplication.Invoke(Action)"/> shim is the documented v2
/// path for this — it posts to the iteration loop.
///
/// Input policy: the bottom <see cref="TextField"/> is the focus target
/// while connected. Disabling (CanFocus=false + ReadOnly=true) when
/// disconnected stops typing from being silently swallowed; the menu
/// remains keyboard-driven via its hotkeys regardless.
/// </remarks>
internal sealed class MainWindow : Window
{
    private const int FrameLogCapacity = 200;
    private const int ChatLogCapacity = 200;

    private readonly IApplication app;
    // myCall / portName / modem / runner are mutable so the Settings dialog
    // can hot-swap them at runtime — see ReconfigureAsync.
    private Callsign myCall;
    private string portName;
    private KissSerialModem modem;
    private SessionRunner runner;
    // Not `readonly`: the View → "Clear ..." menu commands swap in a fresh
    // buffer rather than touching RingBuffer's internals (kept as-is per
    // the brief).
    private RingBuffer frameLog = new(FrameLogCapacity);
    private RingBuffer chatLog = new(ChatLogCapacity);

    private readonly TextView monitorView;
    private readonly TextView chatView;
    private readonly TextField inputField;
    private readonly Shortcut statusIdentity;
    private readonly Shortcut statusPort;
    private readonly Shortcut statusLink;
    private readonly MenuItem acceptIncomingMenuItem;

    private LinkState linkState = LinkState.Disconnected;
    private Callsign? remote;
    private CancellationTokenSource? runnerCts;
    private Callsign? pendingAutoConnect;
    private bool acceptIncoming = true;
    private bool disposed;

    public MainWindow(IApplication app, Callsign myCall, string portName, KissSerialModem modem)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.myCall = myCall;
        this.portName = portName ?? throw new ArgumentNullException(nameof(portName));
        this.modem = modem ?? throw new ArgumentNullException(nameof(modem));

        Title = $"Packet.Term {AppInfo.Version}  —  MYCALL {FormatCallsign(myCall)}  port {portName} @ 57600";
        BorderStyle = LineStyle.None;

        // ─── MenuBar ──────────────────────────────────────────────────
        var menuBar = BuildMenuBar(out acceptIncomingMenuItem);

        // ─── Frame monitor (top ~40%) ─────────────────────────────────
        var monitorFrame = new FrameView
        {
            Title = "Frame monitor",
            X = 0,
            Y = Pos.Bottom(menuBar),
            Width = Dim.Fill(),
            Height = Dim.Percent(40),
        };
        monitorView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true,
            WordWrap = false,
            CanFocus = false,
        };
        monitorView.SchemeName = TuiSchemes.Monitor;
        monitorFrame.Add(monitorView);

        // ─── Conversation (middle, fills above the input + status) ────
        var chatFrame = new FrameView
        {
            Title = "Conversation",
            X = 0,
            Y = Pos.Bottom(monitorFrame),
            Width = Dim.Fill(),
            // 1 for input row + 1 for status bar — Dim.Fill takes the
            // remaining lines minus that fixed footer.
            Height = Dim.Fill(2),
        };
        chatView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true,
            WordWrap = false,
            CanFocus = false,
        };
        chatView.SchemeName = TuiSchemes.Chat;
        chatFrame.Add(chatView);

        // ─── Input line (one row above status) ────────────────────────
        inputField = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ReadOnly = true,
            CanFocus = false,
        };
        inputField.SchemeName = TuiSchemes.Input;
        inputField.Accepting += OnInputAccepting;

        // ─── StatusBar (bottom row) ───────────────────────────────────
        // Identity + port are stored as fields so the Settings-dialog
        // hot-swap path can mutate their Title without rebuilding the bar.
        // F10 is intentionally NOT bound here for Quit — Terminal.Gui v2
        // hardcodes F10 as the MenuBar activator (the Turbo Vision idiom),
        // so a status-bar Shortcut on F10 is shadowed by the framework
        // and never fires. Esc reaches the user reliably from any focus.
        statusIdentity = new Shortcut(Key.Empty, FormatCallsign(myCall), null);
        statusPort = new Shortcut(Key.Empty, portName, null);
        statusLink = new Shortcut(Key.Empty, "DISCONNECTED", null);
        var statusConnect = new Shortcut(Key.F2, "Conn", () => PromptConnect());
        var statusDisconnect = new Shortcut(Key.F3, "Disc", () => InitiateDisconnect());
        var statusQuit = new Shortcut(Key.Esc, "Quit", () => app.RequestStop());

        var statusBar = new StatusBar(new[]
        {
            statusIdentity, statusPort, statusLink,
            statusConnect, statusDisconnect, statusQuit,
        })
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };
        statusBar.SchemeName = TuiSchemes.Status;

        Add(menuBar, monitorFrame, chatFrame, inputField, statusBar);

        // ─── SessionRunner wiring ─────────────────────────────────────
        runner = new SessionRunner(modem, myCall, OnRunnerChatLine, OnRunnerLinkStateChanged);
        runner.FrameTraced += OnRunnerFrameTraced;

        // Defer the pump start + autoconnect until the view is initialised,
        // so any chat lines emitted before the first paint don't race the
        // first draw.
        Initialized += OnInitialized;
    }

    private MenuBar BuildMenuBar(out MenuItem acceptIncomingItem)
    {
        // The MenuItem "Accept incoming" entry needs to be reachable from
        // its click handler to flip its own title between "[on]" and
        // "[off]" — captured by reference and exposed to the caller.
        var settingsItem = new MenuItem("_Settings...", "", PromptSettings)
        {
            Key = Key.S.WithCtrl,
        };
        var exitItem = new MenuItem("E_xit", "", () => app.RequestStop())
        {
            Key = Key.Q.WithCtrl,
        };

        var connectItem = new MenuItem("_Connect...", "", PromptConnect)
        {
            Key = Key.F2,
        };
        var disconnectItem = new MenuItem("_Disconnect", "", InitiateDisconnect)
        {
            Key = Key.F3,
        };
        acceptIncomingItem = new MenuItem("_Accept incoming  [on]", "", ToggleAcceptIncoming);

        var clearMonitorItem = new MenuItem("Clear _frame monitor", "", () =>
        {
            frameLog = new RingBuffer(FrameLogCapacity);
            monitorView.Text = string.Empty;
        });
        var clearChatItem = new MenuItem("Clear _conversation", "", () =>
        {
            chatLog = new RingBuffer(ChatLogCapacity);
            chatView.Text = string.Empty;
        });

        var aboutItem = new MenuItem("_About...", "", ShowAbout);

        return new MenuBar
        {
            Menus = new[]
            {
                new MenuBarItem("_File", new[] { settingsItem, exitItem }),
                new MenuBarItem("_Session", new[] { connectItem, disconnectItem, acceptIncomingItem }),
                new MenuBarItem("_View", new[] { clearMonitorItem, clearChatItem }),
                new MenuBarItem("_Help", new[] { aboutItem }),
            },
        };
    }

    /// <summary>
    /// Park an auto-connect target. The connect is kicked off once the
    /// view is initialised and the listener pump is running — both
    /// happen during <see cref="OnInitialized"/>.
    /// </summary>
    public void AttachAutoConnect(Callsign? target) => pendingAutoConnect = target;

    // ─── Lifecycle ────────────────────────────────────────────────────

    private void OnInitialized(object? sender, EventArgs e)
    {
        // Start the listener pump in the background. The pump's writes to
        // chatLog/frameLog land on its own task; OnRunner* handlers
        // marshal back via app.Invoke before touching the view.
        runnerCts = new CancellationTokenSource();
        _ = runner.Start(runnerCts.Token);

        if (pendingAutoConnect is { } target)
        {
            _ = Task.Run(async () =>
            {
                // Tiny settle so the first paint completes before we add
                // chat noise; matches the old Spectre behaviour.
                await Task.Delay(200).ConfigureAwait(false);
                await DoConnectAsync(target).ConfigureAwait(false);
            });
        }
    }

    // ─── Menu / status handlers ───────────────────────────────────────

    private void PromptConnect()
    {
        if (linkState != LinkState.Disconnected)
        {
            MessageBox.ErrorQuery(app, "Already connected",
                "Disconnect the active session before opening another one.", "OK");
            return;
        }

        var dialog = new ConnectDialog(AppContext.LastConnectTarget ?? string.Empty);
        app.Run(dialog);
        if (dialog.Canceled || dialog.Result is null)
        {
            return;
        }

        var raw = dialog.Result;
        if (!Callsign.TryParse(raw, out var target))
        {
            MessageBox.ErrorQuery(app, "Invalid callsign",
                $"\"{raw}\" doesn't parse as an AX.25 callsign.\nUse e.g. M0LTE-1 or G1AAA-0.", "OK");
            return;
        }

        AppContext.LastConnectTarget = target.ToString();
        AppContext.SaveSettings();

        _ = Task.Run(() => DoConnectAsync(target));
    }

    private async Task DoConnectAsync(Callsign target)
    {
        AppendChat($"*** Connecting to {FormatCallsign(target)}...");
        var outcome = await runner.ConnectAsync(target, TimeSpan.FromSeconds(30), CancellationToken.None)
            .ConfigureAwait(false);
        if (outcome is null)
        {
            AppendChat("*** Connect timed out");
        }
        else if (outcome.Name == "DL_CONNECT_confirm")
        {
            AppendChat($"*** Connected to {FormatCallsign(target)}");
        }
        else
        {
            AppendChat("*** Connect refused / link torn down before UA arrived");
        }
    }

    private void InitiateDisconnect()
    {
        if (linkState != LinkState.Connected && linkState != LinkState.Connecting)
        {
            return;
        }
        _ = Task.Run(() => runner.DisconnectAsync(TimeSpan.FromSeconds(15), CancellationToken.None));
    }

    private void ToggleAcceptIncoming()
    {
        acceptIncoming = !acceptIncoming;
        acceptIncomingMenuItem.Title = acceptIncoming
            ? "_Accept incoming  [on]"
            : "_Accept incoming  [off]";
        // SessionRunner sets AcceptIncoming=false while a session is
        // active; this toggle is the user-facing default for idle state.
        if (linkState == LinkState.Disconnected)
        {
            runner.AcceptIncoming = acceptIncoming;
        }
    }

    private void PromptSettings()
    {
        // Pre-fill with the LIVE config (mutable myCall / portName) rather
        // than the saved settings — the user expects "edit what's currently
        // running", not "edit what's persisted".
        var dialog = new SettingsDialog(FormatCallsign(myCall), portName);
        app.Run(dialog);
        if (dialog.Canceled)
        {
            return;
        }

        var newCallStr = dialog.MyCallResult ?? string.Empty;
        var newPortStr = (dialog.PortResult ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(newCallStr) || string.IsNullOrWhiteSpace(newPortStr))
        {
            MessageBox.ErrorQuery(app, "Settings incomplete",
                "Both MYCALL and serial port must be set.", "OK");
            return;
        }
        if (!Callsign.TryParse(newCallStr, out var newCall))
        {
            MessageBox.ErrorQuery(app, "Invalid callsign",
                $"\"{newCallStr}\" doesn't parse. Settings unchanged.", "OK");
            return;
        }

        // Update the persisted settings (no-op if PersistenceEnabled=false,
        // i.e. CLI-driven instances).
        AppContext.Settings.MyCall = newCallStr;
        AppContext.Settings.SerialPort = newPortStr;
        AppContext.SaveSettings();

        // Hot-swap. Runs on a background task so the UI thread stays
        // responsive; ReconfigureAsync marshals UI updates back via
        // app.Invoke.
        _ = Task.Run(() => ReconfigureAsync(newPortStr, newCall));
    }

    /// <summary>
    /// Apply a live MYCALL / port change without restarting the process.
    /// Disconnects any active session, disposes the runner (and the modem
    /// if the port is changing), reopens the modem on the new port if
    /// needed, builds a fresh runner with the new MYCALL, and resumes
    /// pumping. UI status bar + window title refresh once the new pump
    /// is live.
    /// </summary>
    private async Task ReconfigureAsync(string newPortName, Callsign newMyCall)
    {
        var portChanged = !string.Equals(newPortName, portName, StringComparison.Ordinal);
        var callChanged = !newMyCall.Equals(myCall);
        if (!portChanged && !callChanged)
        {
            app.Invoke(() => AppendChat("*** Settings unchanged."));
            return;
        }

        app.Invoke(() => AppendChat(
            $"*** Reconfiguring: MYCALL={FormatCallsign(newMyCall)} port={newPortName} ..."));

        // 1. Disconnect active session (best-effort; we proceed regardless).
        if (linkState != LinkState.Disconnected)
        {
            try
            {
                await runner.DisconnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // proceed anyway — the runner is about to be disposed
            }
        }

        // 2. Open the new modem FIRST (if port changing). Doing this before
        //    disposing the current modem means a failed open leaves us with
        //    a working configuration to fall back to.
        KissSerialModem newModem = modem;
        if (portChanged)
        {
            try
            {
                newModem = KissSerialModem.Open(newPortName);
            }
            catch (Exception ex)
            {
                app.Invoke(() => MessageBox.ErrorQuery(app, "Modem open failed",
                    $"Couldn't open {newPortName}: {ex.Message}\n\nKeeping current configuration.",
                    "OK"));
                return;
            }
        }

        // 3. Stop + dispose old runner. (Always — the listener is bound
        //    to a specific MyCall at construction, so a call change alone
        //    still requires a rebuild.)
        try { runnerCts?.Cancel(); } catch { /* swallow */ }
        try { runner.Dispose(); } catch { /* swallow */ }
        try { runnerCts?.Dispose(); } catch { /* swallow */ }
        runnerCts = null;

        // 4. Swap modem (if port changed).
        if (portChanged)
        {
            try { modem.Dispose(); } catch { /* swallow */ }
            modem = newModem;
        }

        myCall = newMyCall;
        portName = newPortName;

        // 5. Build the new runner + restart the pump.
        runner = new SessionRunner(modem, myCall, OnRunnerChatLine, OnRunnerLinkStateChanged);
        runner.FrameTraced += OnRunnerFrameTraced;
        runnerCts = new CancellationTokenSource();
        _ = runner.Start(runnerCts.Token);

        // 6. Refresh UI bits the user notices.
        app.Invoke(() =>
        {
            Title = $"Packet.Term {AppInfo.Version}  —  MYCALL {FormatCallsign(myCall)}  port {portName} @ 57600";
            statusIdentity.Title = FormatCallsign(myCall);
            statusPort.Title = portName;
            AppendChat($"*** Reconfigured: MYCALL={FormatCallsign(myCall)} port={portName}");
            // statusbar layout may need a kick if Title widths changed.
            SetNeedsLayout();
        });
    }

    private void ShowAbout()
    {
        var body =
            $"Packet.Term v{AppInfo.Version}\n" +
            "\n" +
            "An AX.25 terminal application for connected-mode sessions\n" +
            "over a KISS-over-USB-serial modem.\n" +
            "\n" +
            "Built on @packet-net/ax25 and Terminal.Gui v2.\n" +
            "MIT licence.\n" +
            "\n" +
            "https://github.com/m0lte/packet.net";
        MessageBox.Query(app, "About Packet.Term", body, "OK");
    }

    // ─── SessionRunner callbacks (called on background threads) ───────

    private void OnRunnerChatLine(string line)
    {
        AppendChat(line);
    }

    private void OnRunnerLinkStateChanged(LinkState state, Callsign? peer)
    {
        app.Invoke(() =>
        {
            linkState = state;
            remote = peer;
            UpdateStatusBar();
            UpdateInputEnabled();
        });
    }

    private void OnRunnerFrameTraced(object? sender, Ax25FrameEventArgs e)
    {
        var direction = e.Direction == Packet.Ax25.Session.FrameDirection.Transmitted
            ? FrameDirection.Transmit
            : FrameDirection.Receive;
        var line = FrameFormatter.Format(direction, e.Frame, DateTimeOffset.Now);
        AppendFrameLine(line);
    }

    // ─── View updaters ────────────────────────────────────────────────

    private void AppendChat(string line)
    {
        var stamped = $"[{DateTimeOffset.Now.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}";
        chatLog.Add(stamped);
        app.Invoke(() =>
        {
            chatView.Text = string.Join("\n", chatLog.Snapshot());
            chatView.MoveEnd();
        });
    }

    private void AppendFrameLine(string line)
    {
        foreach (var sub in line.Split('\n'))
        {
            frameLog.Add(sub);
        }
        app.Invoke(() =>
        {
            monitorView.Text = string.Join("\n", frameLog.Snapshot());
            monitorView.MoveEnd();
        });
    }

    private void UpdateStatusBar()
    {
        statusLink.Title = linkState switch
        {
            LinkState.Connected => $"CONNECTED to {FormatCallsign(remote ?? new Callsign("UNK"))}",
            LinkState.Connecting => $"CONNECTING to {FormatCallsign(remote ?? new Callsign("UNK"))}",
            LinkState.Disconnecting => "DISCONNECTING",
            _ => "DISCONNECTED",
        };
    }

    private void UpdateInputEnabled()
    {
        if (linkState == LinkState.Connected)
        {
            inputField.ReadOnly = false;
            inputField.CanFocus = true;
            inputField.SetFocus();
        }
        else
        {
            inputField.ReadOnly = true;
            inputField.CanFocus = false;
            inputField.Text = string.Empty;
        }
    }

    private void OnInputAccepting(object? sender, CommandEventArgs e)
    {
        // 'Accepting' fires when Enter is hit. Consume the event so
        // Terminal.Gui doesn't try to apply its default activate
        // semantics (which would also fire on the menu/status bar).
        e.Handled = true;
        if (linkState != LinkState.Connected) return;

        var text = inputField.Text ?? string.Empty;
        inputField.Text = string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        AppendChat($"me: {text}");
        var bytes = Encoding.ASCII.GetBytes(text + "\r");
        runner.SendData(bytes);
    }

    // ─── Disposal ─────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            disposed = true;
            try { runnerCts?.Cancel(); } catch { /* swallowed */ }
            try { runner.Dispose(); } catch { /* swallowed */ }
            try { runnerCts?.Dispose(); } catch { /* swallowed */ }
            // MainWindow owns the modem (Program transferred it at
            // construction); dispose it here so the serial port closes.
            try { modem.Dispose(); } catch { /* swallowed */ }
        }
        base.Dispose(disposing);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static string FormatCallsign(Callsign c) => c.Ssid == 0 ? c.Base : c.ToString();
}
