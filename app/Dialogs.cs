namespace Soundboard;

/// <summary>OK / Cancel row at the bottom of a dialog: buttons sized to their text (no clipped labels).</summary>
static class DialogButtons
{
    public static (Button Ok, Button Cancel) Add(Form form, string okText = "OK", string? cancelText = null)
    {
        Button Make(string text, DialogResult result) => new()
        {
            // same look as the other buttons (e.g. "Listen"): sized to the label, nothing bigger
            Text = text, DialogResult = result, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            Margin = new Padding(6, 0, 0, 0),
        };
        var ok = Make(okText, DialogResult.OK);
        var cancel = Make(cancelText ?? T("Cancel"), DialogResult.Cancel);
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 8, 12, 12), WrapContents = false,
        };
        row.Controls.Add(cancel); // right-to-left: Cancel at the far right, OK just before
        row.Controls.Add(ok);
        form.Controls.Add(row);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return (ok, cancel);
    }
}

/// <summary>Captures a key combination. Escape cancels, Delete/Backspace clears.</summary>
sealed class HotkeyDialog : Form
{
    public Hotkey Result { get; private set; } = new();
    readonly Label _label;

    public HotkeyDialog(string title, Hotkey current)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 120);
        KeyPreview = true;
        ShowInTaskbar = false;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 11f),
            Text = T("Press a key…") + "\n\n" +
                   (current.IsSet ? T("Current: {0}   ·   ", current) : "") + T("Delete: none   ·   Esc: cancel"),
        };
        Controls.Add(_label);
        Result = current;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        HandleKey(keyData);
        return true; // swallow everything (Tab, arrows, Enter...)
    }

    void HandleKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;

        if (key == Keys.Escape && (keyData & Keys.Modifiers) == 0)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }
        if ((key is Keys.Delete or Keys.Back) && (keyData & Keys.Modifiers) == 0)
        {
            Result = new Hotkey();
            DialogResult = DialogResult.OK;
            return;
        }

        int mods = 0;
        if ((keyData & Keys.Alt) != 0) mods |= 1;
        if ((keyData & Keys.Control) != 0) mods |= 2;
        if ((keyData & Keys.Shift) != 0) mods |= 4;
        Result = new Hotkey { Modifiers = mods, Key = (int)key };
        DialogResult = DialogResult.OK;
    }
}

sealed class TextDialog : Form
{
    readonly TextBox _box;
    public string Value => _box.Text.Trim();

    public TextDialog(string title, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 100);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);

        _box = new TextBox { Text = initial, Left = 12, Top = 14, Width = 336 };
        Controls.Add(_box);
        DialogButtons.Add(this);
    }
}
