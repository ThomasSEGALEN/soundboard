using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Soundboard;

sealed record MicInfo(string Id, string Name, bool Installed, bool HasBackup);

/// <summary>
/// Installs, repairs and removes the Soundboard APO on microphones — the same steps as
/// scripts/install.ps1 and uninstall.ps1, built into the exe. The module DLL is embedded.
/// Changing the registry needs administrator rights: the app runs itself elevated with
/// "--setup ..." (see <see cref="RunElevated"/>), the window itself never runs as admin.
/// </summary>
static class MicSetup
{
    public const string ApoClsid = "{7C3E5A21-9B4D-4F6E-A1C2-5D8E3F907B14}";
    const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

    const string KeySfxComposite = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
    const string KeySfxLegacy = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
    const string KeySfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
    const string KeyEfxComposite = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
    const string KeyEfxLegacy = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
    const string KeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
    const string KeyDisableSysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    const string KeyFriendly = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    const string KeyDeviceDesc = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    static readonly string[] Modes =
    {
        "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", // default
        "{98951333-B9CD-48B1-A0A3-FF40682D73F7}", // communications
        "{FC1CFC9B-B9D6-4CFA-B5E0-4BB2166878B2}", // speech
        "{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}", // raw (Discord)
    };

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Soundboard");
    static string DllPath => Path.Combine(InstallDir, "SoundboardApo.dll");
    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Soundboard");
    static string BackupDir => Path.Combine(DataDir, "backup");
    static string LogDir => Path.Combine(DataDir, "logs");

    // ------------------------------------------------------------ read-only (no admin needed)

    public static List<MicInfo> ListMicrophones()
    {
        var result = new List<MicInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(CaptureRoot);
        if (root == null) return result;
        foreach (var id in root.GetSubKeyNames())
        {
            using var ep = root.OpenSubKey(id);
            if (ep == null || ((int)(ep.GetValue("DeviceState") ?? 0) & 0xF) != 1) continue; // active only
            using var props = ep.OpenSubKey("Properties");
            using var fx = ep.OpenSubKey("FxProperties");
            if (fx == null) continue; // cannot host effects
            string name = $"{props?.GetValue(KeyFriendly)} ({props?.GetValue(KeyDeviceDesc)})";
            bool installed = fx.GetValue(KeyEfxComposite) is string[] list && list.Contains(ApoClsid, StringComparer.OrdinalIgnoreCase);
            result.Add(new MicInfo(id, name, installed, File.Exists(Path.Combine(BackupDir, id + ".json"))));
        }
        return result.OrderByDescending(m => m.Installed).ThenBy(m => m.Name).ToList();
    }

    /// <summary>Microphones the module was installed on before (they have a backup): what "Repair" reinstalls.</summary>
    public static List<string> PreviouslyInstalled() =>
        Directory.Exists(BackupDir)
            ? Directory.GetFiles(BackupDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList()
            : new List<string>();

    // ------------------------------------------------------------ elevation

    /// <summary>Runs "Soundboard.exe --setup args" as administrator and returns (ok, message).</summary>
    public static (bool Ok, string Message) RunElevated(params string[] args)
    {
        string log = Path.Combine(Path.GetTempPath(), $"soundboard-setup-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--setup");
        psi.ArgumentList.Add(log);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            string msg = File.Exists(log) ? File.ReadAllText(log).Trim() : "";
            try { File.Delete(log); } catch { }
            return (p.ExitCode == 0, msg);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, T("Administrator permission was refused."));
        }
    }

    /// <summary>Entry point of the elevated process: --setup &lt;log&gt; install|remove|uninstall [ids...]</summary>
    public static int RunSetupCommand(string[] args)
    {
        string log = args[1];
        var output = new List<string>();
        try
        {
            Legacy.MigrateMicBackups(BackupDir); // backups made when the app was called SoundMix
            string cmd = args[2];
            var ids = args.Skip(3).ToList();
            switch (cmd)
            {
                case "install": Install(ids, output); break;
                case "remove": Remove(ids, removeModule: false, output); break;
                case "uninstall": Remove(PreviouslyInstalled(), removeModule: true, output); break;
                default: throw new ArgumentException("unknown command: " + cmd);
            }
            File.WriteAllLines(log, output);
            return 0;
        }
        catch (Exception ex)
        {
            output.Add(T("Error: {0}", ex.Message));
            try { File.WriteAllLines(log, output); } catch { }
            return 1;
        }
    }

    // ------------------------------------------------------------ admin operations

    static void Install(List<string> ids, List<string> output)
    {
        StopAudio();
        try
        {
            Directory.CreateDirectory(InstallDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(LogDir);
            // audiodg runs as LOCAL SERVICE: let it write its diagnostic log
            var acl = new DirectoryInfo(LogDir).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(LogDir).SetAccessControl(acl);

            ExtractDll();
            CallDllExport("DllRegisterServer");

            foreach (var id in ids)
            {
                using var fx = OpenValues($@"{CaptureRoot}\{id}\FxProperties");
                using var props = OpenValues($@"{CaptureRoot}\{id}\Properties");
                if (fx == null || props == null) { output.Add(T("Skipped (not supported): {0}", id)); continue; }

                var backup = LoadOrCreateBackup(id, fx, props);

                // remove the stream-effect placement used by very early versions
                if (fx.GetValue(KeySfxComposite) is string[] sfx && sfx.Contains(ApoClsid, StringComparer.OrdinalIgnoreCase))
                    Restore(fx, KeySfxComposite, backup["Composite"]);
                if (ApoClsid.Equals(fx.GetValue(KeySfxLegacy) as string, StringComparison.OrdinalIgnoreCase))
                    Restore(fx, KeySfxLegacy, backup["Legacy"]);
                Restore(fx, KeySfxModes, backup["Modes"]);

                // endpoint effect: keep existing effects, append ours
                var list = new List<string>();
                if (fx.GetValue(KeyEfxComposite) is string[] cur) list.AddRange(cur);
                else if (fx.GetValue(KeyEfxLegacy) is string leg && leg != ApoClsid && !leg.StartsWith("{00000000")) list.Add(leg);
                if (!list.Contains(ApoClsid, StringComparer.OrdinalIgnoreCase)) list.Add(ApoClsid);
                fx.SetValue(KeyEfxComposite, list.ToArray(), RegistryValueKind.MultiString);
                if (fx.GetValue(KeyEfxLegacy) == null) fx.SetValue(KeyEfxLegacy, ApoClsid, RegistryValueKind.String);

                var modes = new List<string>(fx.GetValue(KeyEfxModes) as string[] ?? Array.Empty<string>());
                foreach (var m in Modes) if (!modes.Contains(m, StringComparer.OrdinalIgnoreCase)) modes.Add(m);
                fx.SetValue(KeyEfxModes, modes.ToArray(), RegistryValueKind.MultiString);

                if (props.GetValue(KeyDisableSysFx) is int sys && sys != 0)
                    props.SetValue(KeyDisableSysFx, 0, RegistryValueKind.DWord);

                output.Add(T("Installed: {0}", id));
            }
        }
        finally
        {
            RestartAudio();
            Legacy.RemoveOldModuleFiles(); // the CLSID now points at the new DLL
        }
    }

    static void Remove(List<string> ids, bool removeModule, List<string> output)
    {
        StopAudio();
        try
        {
            foreach (var id in ids)
            {
                string file = Path.Combine(BackupDir, id + ".json");
                if (!File.Exists(file)) continue;
                var b = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                using (var fx = OpenValues($@"{CaptureRoot}\{id}\FxProperties"))
                {
                    if (fx != null)
                    {
                        Restore(fx, KeySfxComposite, b["Composite"]);
                        Restore(fx, KeySfxLegacy, b["Legacy"]);
                        Restore(fx, KeySfxModes, b["Modes"]);
                        Restore(fx, KeyEfxComposite, b["EfxComposite"]);
                        Restore(fx, KeyEfxLegacy, b["EfxLegacy"]);
                        Restore(fx, KeyEfxModes, b["EfxModes"]);
                    }
                }
                using (var props = OpenValues($@"{CaptureRoot}\{id}\Properties"))
                    if (props != null) Restore(props, KeyDisableSysFx, b["SysFx"]);
                File.Delete(file);
                output.Add(T("Restored: {0}", id));
            }

            if (removeModule && File.Exists(DllPath))
            {
                try { CallDllExport("DllUnregisterServer"); } catch { }
                for (int i = 0; i < 10; i++)
                {
                    try { File.Delete(DllPath); break; }
                    catch (IOException) { Thread.Sleep(500); } // audiodg may still be releasing it
                    catch (UnauthorizedAccessException) { Thread.Sleep(500); }
                }
                try { if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir); } catch { }
                output.Add(File.Exists(DllPath) ? T("Module still present (locked); it will be replaced at the next install.") : T("Module removed."));
            }
            if (removeModule && Directory.Exists(Legacy.OldModuleDir) && !File.Exists(DllPath))
            {
                // module from the SoundMix days: unregister it through its own DLL, then delete it
                string oldDll = Path.Combine(Legacy.OldModuleDir, "SoundMixApo.dll");
                if (File.Exists(oldDll))
                    try { CallDllExport("DllUnregisterServer", oldDll); } catch { }
            }
        }
        finally
        {
            RestartAudio();
            if (removeModule) Legacy.RemoveOldModuleFiles();
        }
    }

    // ------------------------------------------------------------ helpers

    // Administrators only have SetValue + ReadKey on these keys: never ask for more.
    static RegistryKey? OpenValues(string path) =>
        Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.SetValue | RegistryRights.ReadKey);

    static JsonNode? Saved(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name)) return null;
        var kind = key.GetValueKind(name);
        object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new JsonObject
        {
            ["Kind"] = kind.ToString(),
            ["Value"] = JsonSerializer.SerializeToNode(v),
        };
    }

    /// <summary>Backup of the original values, taken once (compatible with the PowerShell scripts).</summary>
    static JsonObject LoadOrCreateBackup(string id, RegistryKey fx, RegistryKey props)
    {
        string file = Path.Combine(BackupDir, id + ".json");
        if (File.Exists(file)) return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var b = new JsonObject
        {
            ["Id"] = id,
            ["Composite"] = Saved(fx, KeySfxComposite),
            ["Legacy"] = Saved(fx, KeySfxLegacy),
            ["Modes"] = Saved(fx, KeySfxModes),
            ["EfxComposite"] = Saved(fx, KeyEfxComposite),
            ["EfxLegacy"] = Saved(fx, KeyEfxLegacy),
            ["EfxModes"] = Saved(fx, KeyEfxModes),
            ["SysFx"] = Saved(props, KeyDisableSysFx),
        };
        File.WriteAllText(file, b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return b;
    }

    /// <summary>Puts a value back as it was; a missing/null backup means "the value did not exist".</summary>
    static void Restore(RegistryKey key, string name, JsonNode? saved)
    {
        if (saved is not JsonObject o || o["Kind"] is null)
        {
            if (key.GetValueNames().Contains(name)) key.DeleteValue(name);
            return;
        }
        var kind = Enum.Parse<RegistryValueKind>(o["Kind"]!.GetValue<string>());
        var v = o["Value"];
        object value = kind switch
        {
            RegistryValueKind.MultiString => v is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToArray() : new[] { v!.GetValue<string>() },
            RegistryValueKind.DWord => v!.GetValue<int>(),
            _ => v!.GetValue<string>(),
        };
        key.SetValue(name, value, kind);
    }

    static void ExtractDll()
    {
        using var res = typeof(MicSetup).Assembly.GetManifestResourceStream("SoundboardApo.dll")
                        ?? throw new InvalidOperationException("module missing from the exe");
        for (int i = 0; ; i++)
        {
            try
            {
                using var f = File.Create(DllPath);
                res.CopyTo(f);
                return;
            }
            catch (IOException) when (i < 10) { Thread.Sleep(500); res.Position = 0; } // audiodg releasing it
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DllRegisterFn();

    static void CallDllExport(string name, string? dll = null)
    {
        IntPtr lib = NativeLibrary.Load(dll ?? DllPath);
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<DllRegisterFn>(NativeLibrary.GetExport(lib, name));
            int hr = fn();
            if (hr < 0) throw new InvalidOperationException($"{name} failed (0x{hr:X8})");
        }
        finally { NativeLibrary.Free(lib); }
    }

    static void StopAudio()
    {
        using var audio = new ServiceController("Audiosrv");
        if (audio.Status != ServiceControllerStatus.Stopped)
        {
            audio.Stop();
            audio.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>AudioEndpointBuilder caches the endpoint properties: restart it, then Audiosrv.</summary>
    static void RestartAudio()
    {
        using var aeb = new ServiceController("AudioEndpointBuilder");
        using var audio = new ServiceController("Audiosrv");
        try
        {
            audio.Refresh();
            if (audio.Status != ServiceControllerStatus.Stopped)
            {
                audio.Stop();
                audio.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            aeb.Stop();
            aeb.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch { /* if a stop fails, still try to start both */ }
        aeb.Refresh();
        if (aeb.Status != ServiceControllerStatus.Running) { aeb.Start(); aeb.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
        audio.Refresh();
        if (audio.Status != ServiceControllerStatus.Running) { audio.Start(); audio.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
    }
}
