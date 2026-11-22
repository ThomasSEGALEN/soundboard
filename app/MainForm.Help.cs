namespace Soundboard;

// F1 / "?": every gesture and shortcut on one page.
sealed partial class MainForm
{
    // English texts, translated when shown (see Loc.French.cs)
    static readonly (string Section, (string Gesture, string Effect)[] Rows)[] HelpContent =
    {
        ("Play", new[]
        {
            ("Click", "Plays the sound (or does what “If already playing” says when it is playing)"),
            ("Middle click", "Stops this sound"),
            ("Your hotkey", "Plays the sound, even in a game"),
            ("⏯ Pause · ⏹ Stop", "All sounds (their keys are set in Settings → Hotkeys)"),
        }),
        ("Select", new[]
        {
            ("Ctrl + click", "Adds or removes a sound from the selection"),
            ("Shift + click", "Selects a range of sounds"),
            ("Ctrl + A", "Selects the whole category"),
            ("Esc", "Clears the selection"),
        }),
        ("Organise", new[]
        {
            ("Right click", "Favourite, category, colour, hotkey, edit, delete"),
            ("Drag a sound", "Moves it; onto a category: files it there"),
            ("Drag a category", "Changes the order of the categories"),
            ("Drop files / Ctrl + V", "Adds sounds (files, folder or pack)"),
        }),
        ("Keyboard", new[]
        {
            ("⌨ Keys", "Click a sound, then press its key; the next one is picked for you"),
            ("F2", "Renames the hovered or selected sound"),
            ("Delete", "Deletes the selection"),
            ("Ctrl + Z", "Undoes the last change"),
            ("Ctrl + F", "Goes to the search box (Esc clears it)"),
            ("F1", "This help"),
        }),
    };

    void ShowHelp()
    {
        using var f = BuildHelpForm();
        f.ShowDialog(this);
    }

    internal Form BuildHelpForm()
    {
        var f = new Form
        {
            Text = T("Help"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = Font, KeyPreview = true,
        };
        f.KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.F1) f.Close(); };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 12, 18, 4),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        foreach (var (section, rows) in HelpContent)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title = new Label
            {
                Text = T(section).ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f),
                Tag = "keep-color", ForeColor = Theme.Current.SubText, Margin = new Padding(0, row == 0 ? 0 : 12, 0, 4),
            };
            grid.Controls.Add(title, 0, row);
            grid.SetColumnSpan(title, 2);
            row++;
            foreach (var (gesture, effect) in rows)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label { Text = T(gesture), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 3, 18, 3) }, 0, row);
                grid.Controls.Add(new Label { Text = T(effect), AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 3, 0, 3) }, 1, row);
                row++;
            }
        }
        f.Controls.Add(grid);
        var (ok, _) = DialogButtons.Add(f, T("Close"));
        ok.Parent!.Controls.Remove(f.CancelButton as Control); // a single "Close" button
        f.CancelButton = ok;

        var g = grid.GetPreferredSize(new Size(int.MaxValue, int.MaxValue));
        var b = ok.Parent.GetPreferredSize(new Size(g.Width, int.MaxValue));
        f.ClientSize = new Size(g.Width, g.Height + b.Height);
        Theme.Apply(f);
        return f;
    }
}
