using System.Diagnostics;

namespace Soundboard;

static class Program
{
    const string ShowEventName = @"Local\SoundboardApp_Show";

    // Command line:
    //   (none)            normal start; if this exe is not the installed copy, offers to install it
    //   --install [--silent]   install / update this exe, then start the installed copy
    //   --uninstall-app   full uninstall (used by Windows "Installed apps")
    //   --setup ...       elevated helper for the microphone module (see MicSetup)
    //   --tray            start hidden near the clock (launch with Windows)
    //   --first-run       show the welcome / microphone choice
    //   --portable        never offer to install
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--setup") { Loc.Init(AppConfig.Load().Language); return MicSetup.RunSetupCommand(args); }

        ApplicationConfiguration.Initialize();
        Loc.Init(AppConfig.Load().Language); // interface language (also for the install and uninstall messages)

        if (args.Contains("--uninstall-app")) return Installer.UninstallInteractive(quiet: args.Contains("--quiet"));

        bool silentInstall = args.Contains("--install") && args.Contains("--silent");
        if (args.Contains("--install") || (!Installer.IsInstalledCopy && !args.Contains("--portable") && OfferInstall()))
        {
            try { Installer.Install(); }
            catch (Exception ex)
            {
                MessageBox.Show(T("Installation failed:") + "\n" + ex.Message, "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            Process.Start(new ProcessStartInfo(Installer.InstalledExe, silentInstall ? "" : "--first-run") { UseShellExecute = true });
            return 0;
        }

        using var single = new Mutex(true, @"Local\SoundboardApp_SingleInstance", out bool first);
        if (!first)
        {
            // Already running (maybe hidden near the clock): ask it to show its window.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
                MessageBox.Show(T("Soundboard is already running."), "Soundboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        var form = new MainForm(startHidden: args.Contains("--tray"), firstRun: args.Contains("--first-run"));

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);
        StartListener(showEvent, () => form.BeginInvoke(form.ShowFromTray));
        StartListener(quitEvent, () => form.BeginInvoke(form.QuitApp)); // installer update / uninstall

        Application.AddMessageFilter(new InputModeFilter()); // focus rings only for keyboard users
        Application.Run(form);

        // "Restore a backup": start again once this copy has released the single-instance lock
        if (RestartRequested)
        {
            single.ReleaseMutex();
            single.Dispose();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        }
        return 0;
    }

    public static bool RestartRequested;

    static void StartListener(EventWaitHandle ev, Action onSignal)
    {
        new Thread(() =>
        {
            while (ev.WaitOne())
            {
                try { onSignal(); } catch { return; }
            }
        }) { IsBackground = true, Name = "Soundboard event listener" }.Start();
    }

    /// <summary>Started from a downloaded / copied exe: install it (or update the installed one)?</summary>
    static bool OfferInstall()
    {
        string text = Installer.IsInstalled
            ? T("A version of Soundboard is already installed.\n\nReplace it with this one?")
            : T("Install Soundboard on this PC?\n\nA shortcut will be added to the Desktop and the Start menu.");
        var r = MessageBox.Show(text, "Soundboard " + Installer.Version, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) Environment.Exit(0);
        return r == DialogResult.Yes; // No = run this copy without installing
    }
}
