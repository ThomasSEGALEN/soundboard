namespace Soundboard;

/// <summary>Choose the microphones that carry Soundboard, repair, or uninstall everything.</summary>
sealed class MicDialog : Form
{
    readonly CheckedListBox _list;
    readonly Label _info;
    readonly Button _apply, _repair, _uninstall, _close;
    List<MicInfo> _mics = new();

    public MicDialog()
    {
        Text = T("Microphone");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(470, 300);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label { Text = T("Microphones that carry Soundboard:"), AutoSize = true, Location = new Point(14, 12) };
        _list = new CheckedListBox { Location = new Point(14, 36), Size = new Size(442, 150), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle };
        _info = new Label
        {
            Location = new Point(14, 192), Size = new Size(442, 52), Tag = "keep-color",
            Text = T("Tick the microphone you use in Discord and your games.\nWindows asks for administrator rights, and audio cuts out for 2 seconds."),
        };
        _apply = new Button { Text = T("Apply"), Location = new Point(14, 254), AutoSize = true };
        _repair = new Button { Text = T("Repair"), Location = new Point(120, 254), AutoSize = true };
        _uninstall = new Button { Text = T("Uninstall"), Location = new Point(226, 254), AutoSize = true };
        _close = new Button { Text = T("Close"), Location = new Point(370, 254), AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = _close;

        _apply.Click += (_, _) => Apply();
        _repair.Click += (_, _) => Run(() => Repair(), T("Repairing…"));
        _uninstall.Click += (_, _) =>
        {
            if (MessageBox.Show(this, T("Remove Soundboard from every microphone and restore their original settings?"),
                    T("Uninstall"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                Run(() => MicSetup.RunElevated("uninstall"), T("Uninstalling…"));
        };

        Controls.AddRange(new Control[] { title, _list, _info, _apply, _repair, _uninstall, _close });
        _info.ForeColor = Theme.Current.SubText;
        Reload();
    }

    void Reload()
    {
        _mics = MicSetup.ListMicrophones();
        _list.Items.Clear();
        foreach (var m in _mics) _list.Items.Add(m.Name, m.Installed);
        _uninstall.Enabled = _mics.Any(m => m.Installed || m.HasBackup);
    }

    /// <summary>Reinstalls on the microphones chosen before (after a driver or Windows update).</summary>
    public static (bool Ok, string Message) Repair()
    {
        var ids = MicSetup.PreviouslyInstalled();
        if (ids.Count == 0) return (false, T("No microphone chosen yet: tick one, then “Apply”."));
        return MicSetup.RunElevated(new[] { "install" }.Concat(ids).ToArray());
    }

    void Apply()
    {
        var add = new List<string>();
        var remove = new List<string>();
        for (int i = 0; i < _mics.Count; i++)
        {
            bool want = _list.GetItemChecked(i);
            if (want && !_mics[i].Installed) add.Add(_mics[i].Id);
            if (!want && _mics[i].Installed) remove.Add(_mics[i].Id);
        }
        if (add.Count == 0 && remove.Count == 0) { Close(); return; }
        Run(() =>
        {
            var r1 = add.Count > 0 ? MicSetup.RunElevated(new[] { "install" }.Concat(add).ToArray()) : (true, "");
            if (!r1.Item1) return r1;
            return remove.Count > 0 ? MicSetup.RunElevated(new[] { "remove" }.Concat(remove).ToArray()) : r1;
        }, T("Applying…"));
    }

    async void Run(Func<(bool Ok, string Message)> action, string busyText)
    {
        foreach (var b in new[] { _apply, _repair, _uninstall, _close }) b.Enabled = false;
        UseWaitCursor = true;
        _info.Text = busyText;
        var (ok, msg) = await Task.Run(action);
        UseWaitCursor = false;
        foreach (var b in new[] { _apply, _repair, _close }) b.Enabled = true;
        Reload();
        _info.ForeColor = ok ? Theme.Current.Good : Theme.Current.Bad;
        _info.Text = ok ? T("Done. Apps can take a few seconds to reconnect to the microphone.")
                        : (string.IsNullOrWhiteSpace(msg) ? T("Failed.") : msg);
    }
}
