namespace Soundboard;

// "⌨ Keys" mode: click a sound, press a key, the next sound is selected — and
// one shared rule for key conflicts (swap / take it / cancel).
sealed partial class MainForm
{
    bool _assignMode;
    SoundEntry? _assignTarget;
    CheckBox? _assignToggle;

    /// <summary>The "⌨ Keys" toggle of the top bar.</summary>
    CheckBox CreateAssignToggle()
    {
        _assignToggle = new CheckBox
        {
            Text = T("⌨ Keys"), Appearance = Appearance.Button, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(3, 3, 3, 3), MinimumSize = new Size(0, _iconSize), // same height as the buttons
        };
        _assignToggle.CheckedChanged += (_, _) => SetAssignMode(_assignToggle.Checked);
        _tips.SetToolTip(_assignToggle, T("Assign hotkeys: click a sound, press a key,\nthen the next sound is picked for you."));
        return _assignToggle;
    }

    void SetAssignMode(bool on)
    {
        if (_assignMode == on) return;
        _assignMode = on;
        if (_assignToggle != null && _assignToggle.Checked != on) _assignToggle.Checked = on;
        _assignTarget = null;
        ClearSelection();
        if (on)
        {
            // global hotkeys swallow their keys before the window sees them: pause them while assigning
            UnregisterAllHotkeys();
            ActiveControl = null; // keys must not go to the search box
            _assignTarget = _buttons.FirstOrDefault()?.Entry;
            Toast(T("Keys mode: click a sound, then press its key · Delete clears · Esc to finish"), 0);
        }
        else
        {
            Toast(null, 0);
            SaveConfig();
            RegisterAllHotkeys();
        }
        SyncAssignView();
    }

    void SyncAssignView()
    {
        foreach (var b in _buttons)
        {
            b.AssignMode = _assignMode;
            b.IsAssignTarget = _assignMode && b.Entry == _assignTarget;
        }
    }

    void SetAssignTarget(SoundEntry s)
    {
        _assignTarget = s;
        SyncAssignView();
    }

    void AdvanceAssignTarget()
    {
        var order = _buttons.Select(b => b.Entry).ToList();
        int i = _assignTarget == null ? -1 : order.IndexOf(_assignTarget);
        if (i >= 0 && i + 1 < order.Count) _assignTarget = order[i + 1];
        else Toast(T("Every sound in this category is done · Esc to finish, or click a sound to change it"), 0);
        SyncAssignView();
    }

    /// <summary>Keys typed while the mode is on (called first by ProcessCmdKey).</summary>
    bool HandleAssignKey(Keys keyData)
    {
        if (!_assignMode) return false;
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None) return true;
        if (key == Keys.Escape && mods == 0) { SetAssignMode(false); return true; }
        if (_assignTarget == null) return true;

        if ((key is Keys.Delete or Keys.Back) && mods == 0)
        {
            _assignTarget.Hotkey = new Hotkey();
            SaveConfig();
            // repaint now: on the last sound the selection does not move, so nothing else would redraw it
            foreach (var b in _buttons.Where(b => b.Entry == _assignTarget)) b.Invalidate();
            AdvanceAssignTarget();
            return true;
        }

        int m = 0;
        if ((mods & Keys.Alt) != 0) m |= 1;
        if ((mods & Keys.Control) != 0) m |= 2;
        if ((mods & Keys.Shift) != 0) m |= 4;
        if (AssignKey(_assignTarget, new Hotkey { Modifiers = m, Key = (int)key }, register: false))
            AdvanceAssignTarget();
        return true;
    }

    // ------------------------------------------------------------ conflicts (shared by every way of setting a key)

    /// <summary>Who already uses this key: a sound, Pause, Stop or push-to-talk (null if free).</summary>
    (string Name, Action<Hotkey> Give)? KeyOwner(Hotkey k, SoundEntry? except)
    {
        if (!k.IsSet) return null;
        bool Same(Hotkey h) => h.IsSet && h.Key == k.Key && h.Modifiers == k.Modifiers;
        if (Same(_config.PauseHotkey)) return (T("Pause"), h => _config.PauseHotkey = h);
        if (Same(_config.StopHotkey)) return (T("Stop"), h => _config.StopHotkey = h);
        if (_config.PushToTalkKey.IsSet && _config.PushToTalkKey.Key == k.Key && k.Modifiers == 0)
            return (T("Push-to-talk"), h => { _config.PushToTalkKey = new Hotkey { Key = h.Key }; if (!h.IsSet) _config.PushToTalk = false; });
        var other = _config.Sounds.FirstOrDefault(o => o != except && Same(o.Hotkey));
        return other == null ? null : (other.Name, h => other.Hotkey = h);
    }

    /// <summary>
    /// Gives `key` to a sound. If something else uses it, asks: swap the two keys, take it away from
    /// the other one, or cancel. Returns false when cancelled.
    /// </summary>
    bool AssignKey(SoundEntry s, Hotkey key, bool register = true)
    {
        if (KeyOwner(key, s) is { } owner)
        {
            var old = s.Hotkey;
            var swap = new TaskDialogButton(old.IsSet ? T("Swap (“{0}” gets {1})", owner.Name, old) : T("Swap"));
            var take = new TaskDialogButton(T("Take it from “{0}”", owner.Name));
            var cancel = TaskDialogButton.Cancel;
            var page = new TaskDialogPage
            {
                Caption = "Soundboard",
                Heading = T("The key {0} is already in use", key),
                Text = T("It triggers “{0}”.", owner.Name),
                Icon = TaskDialogIcon.Warning,
                Buttons = { swap, take, cancel },
                DefaultButton = swap,
            };
            if (!old.IsSet) page.Buttons.Remove(swap); // nothing to give back
            var choice = SuppressDialogs ? take : TaskDialog.ShowDialog(this, page);
            if (choice == cancel) return false;
            owner.Give(choice == swap ? old : new Hotkey());
        }
        s.Hotkey = key;
        SaveConfig();
        foreach (var b in _buttons) b.Invalidate();
        if (register) RegisterAllHotkeys();
        return true;
    }

    // For the tests: drive the mode like a user would (toggle, then key presses).
    internal void TestSetAssignMode(bool on) => SetAssignMode(on);
    internal bool TestPressKey(Keys keyData) => HandleAssignKey(keyData);
    internal string? TestAssignTarget => _assignTarget?.Name;
    internal AppConfig TestConfig => _config;
    internal bool TestToolKey(Keys keyData) => HandleToolKeys(keyData);
    internal void TestUndo() => Undo();
    internal (bool Visible, string Text, bool UndoShown) TestToast =>
        (_toastBar?.Visible == true, _toastText?.Text ?? "", _toastUndo?.Visible == true);
    internal string TestDenoiseLabel => _denoiseButton.Text;

    /// <summary>Right-click → "Raccourci : …": key capture, then the same conflict rule.</summary>
    void QuickHotkey(SoundEntry s)
    {
        UnregisterAllHotkeys(); // so the current combo can be captured again
        try
        {
            using var dlg = new HotkeyDialog(T("Hotkey: {0}", s.Name), s.Hotkey);
            Theme.Apply(dlg);
            if (dlg.ShowDialog(this) == DialogResult.OK) AssignKey(s, dlg.Result, register: false);
        }
        finally
        {
            RebuildGrid(); // re-registers every hotkey
        }
    }
}
