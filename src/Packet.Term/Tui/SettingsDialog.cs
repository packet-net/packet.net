using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Packet.Term.Tui;

/// <summary>
/// Modal "Settings" dialog — edit MYCALL and serial port. The dialog is
/// purely a form; the caller (MainWindow.PromptSettings) persists changes
/// via <see cref="AppContext.SaveSettings"/> and applies them at runtime
/// via MainWindow's reconfigure flow — the listener is disposed and
/// rebuilt, the modem is reopened if the port changes, and any active
/// session is dropped.
/// </summary>
internal sealed class SettingsDialog : Dialog
{
    private readonly TextField myCallField;
    private readonly TextField portField;

    /// <summary>Whether the user cancelled the dialog. (Hides the
    /// inherited <see cref="Dialog.Canceled"/>; we drive it from our
    /// own OK / Cancel handlers.)</summary>
    public new bool Canceled { get; private set; } = true;

    /// <summary>The MYCALL value the user submitted (or <c>null</c> if cancelled).</summary>
    public string? MyCallResult { get; private set; }

    /// <summary>The serial port value the user submitted (or <c>null</c> if cancelled).</summary>
    public string? PortResult { get; private set; }

    public SettingsDialog(string initialMyCall, string initialPort)
    {
        Title = "Settings";
        Width = 56;
        Height = 12;

        var myCallLabel = new Label
        {
            X = 1,
            Y = 1,
            Text = "MYCALL (callsign + SSID, e.g. M0LTE-1):",
        };
        myCallField = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Text = initialMyCall ?? string.Empty,
        };

        var portLabel = new Label
        {
            X = 1,
            Y = 4,
            Text = "Serial port (e.g. /dev/ttyUSB0 or COM5):",
        };
        portField = new TextField
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(2),
            Text = initialPort ?? string.Empty,
        };

        var note = new Label
        {
            X = 1,
            Y = 7,
            Text = "Changes apply immediately. Active session will be dropped.",
        };

        var ok = new Button
        {
            Text = "_OK",
            IsDefault = true,
            X = Pos.Center() - 8,
            Y = Pos.AnchorEnd(2),
        };
        ok.Accepting += (_, e) =>
        {
            e.Handled = true;
            Canceled = false;
            MyCallResult = myCallField.Text;
            PortResult = portField.Text;
            App?.RequestStop(this);
        };

        var cancel = new Button
        {
            Text = "_Cancel",
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(2),
        };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            Canceled = true;
            App?.RequestStop(this);
        };

        Add(myCallLabel, myCallField, portLabel, portField, note, ok, cancel);

        KeyDown += (_, e) =>
        {
            if (e == Key.Esc)
            {
                Canceled = true;
                App?.RequestStop(this);
                e.Handled = true;
            }
        };
    }
}
