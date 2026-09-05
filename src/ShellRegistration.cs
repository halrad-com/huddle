using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Huddle;

/// <summary>
/// Windows shell entry: Start-menu shortcut + AUMID + App Paths, per-user, no admin,
/// idempotent (`huddle --register` / `--unregister`). Ported from the proven MBXS
/// prototype (corelib.Shell/AppIdentity.cs) and trimmed: no Apps &amp; Features entry
/// (a clone-and-setup dev tool's uninstall is --unregister), no Run-at-logon (an
/// orchestrator must not autostart; crashes should be visible), no IconUri PNG
/// (that format serves toast/SMTC surfaces huddle does not have — the shortcut and
/// window icon come from the exe's embedded icon).
/// Spec: docs/superpowers/specs/2026-08-31-shell-registration-design.md.
/// </summary>
public static class ShellRegistration
{
    public const string Aumid = "HALRAD.Huddle";
    public const string DisplayName = "huddle";
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\huddle.exe";
    private const string AumidKey = @"Software\Classes\AppUserModelId\" + Aumid;

    /// <summary>Everything a registration will write, computed pure for tests.</summary>
    public sealed record RegistrationPlan(
        string ExePath, string WorkingDir, string ShortcutPath,
        string SwitcherShortcutPath,
        string AumidKeyPath, string AppPathsKeyPath);

    public static RegistrationPlan Plan(string exePath, string workingDir) => new(
        exePath,
        workingDir,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "huddle.lnk"),
        // The pinnable one. Separate entry rather than a second target on the same
        // shortcut, because pinning is per-shortcut and the operator pins this one.
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Huddle Sessions.lnk"),
        AumidKey,
        AppPathsKey);

    // ---- CLI entry points (Program.cs dispatch: --register / --unregister) --------

    public static int RunRegister(string[] args, Action<string> log)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        { log("register: cannot determine the running exe path"); return 3; }

        // The shortcut's working dir is the repo root — the directory whose
        // huddle.json this process would load. Registering from anywhere else would
        // hand Start-menu launches a cwd where first-run bootstrap TEMPLATES a new
        // config; refuse instead.
        var configPath = Path.GetFullPath(ConfigPathResolver.Resolve(args));
        if (!File.Exists(configPath))
        { log($"register: no config at {configPath} — run --register from your huddle repo root"); return 2; }

        var plan = Plan(exePath, Path.GetDirectoryName(configPath)!);
        Apply(plan, log);
        log($"registered: Start menu 'huddle' -> {plan.ExePath}");
        log($"            Start menu 'Huddle Sessions' -> {plan.ExePath} --peek  (pin this one)");
        log($"  working dir: {plan.WorkingDir}");
        log("  Win+R / shell 'huddle' resolves via App Paths; re-run --register after moving the repo");
        return 0;
    }

    public static int RunUnregister(Action<string> log)
    {
        var plan = Plan("unused", "unused");
        try { if (File.Exists(plan.ShortcutPath)) File.Delete(plan.ShortcutPath); }
        catch (Exception ex) { log($"unregister: could not remove shortcut: {ex.Message}"); }
        try { if (File.Exists(plan.SwitcherShortcutPath)) File.Delete(plan.SwitcherShortcutPath); }
        catch (Exception ex) { log($"unregister: could not remove switcher shortcut: {ex.Message}"); }
        try { Registry.CurrentUser.DeleteSubKeyTree(AumidKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { log($"unregister: could not remove AUMID key: {ex.Message}"); }
        try { Registry.CurrentUser.DeleteSubKeyTree(AppPathsKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { log($"unregister: could not remove App Paths key: {ex.Message}"); }
        log("unregistered: Start-menu shortcuts, AUMID and App Paths entries removed");
        log("  huddle re-registers itself on startup — to keep it off, run: huddle --set shellRegistration false");
        return 0;
    }

    /// <summary>
    /// The repo root a prior --register recorded (App Paths "WorkingDir"), or null.
    /// Read by ConfigPathResolver's last-resort fallback so `huddle` launched from a
    /// config-less cwd (Win+R) boots the registered huddle instead of templating a
    /// fresh huddle.json wherever it happens to land.
    /// </summary>
    public static string? RegisteredRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppPathsKey);
            return key?.GetValue("WorkingDir") as string;
        }
        catch { return null; }
    }

    /// <summary>Best-effort process AUMID (prototype's SetProcessAumid). For a console
    /// app the SHORTCUT's embedded AUMID is what shapes taskbar identity; this call is
    /// cheap and harmless, so mirror the prototype without claiming more.</summary>
    public static void TrySetProcessAumid()
    {
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { }
    }

    // ---- Self-registration (startup heal) -----------------------------------------

    /// <summary>What a startup health check decided, computed pure for tests.</summary>
    public sealed record HealthCheck(bool ShouldRegister, string Reason);

    private static readonly HealthCheck Healthy = new(false, "");

    /// <summary>
    /// Whether startup should (re)write the shell entry. Registers when nothing is
    /// there, heals when what IS there is broken (repo moved, shortcut deleted), and
    /// otherwise leaves it alone. Two deliberate refusals:
    /// a HEALTHY registration pointing at a different exe is never hijacked (a second
    /// clone must not steal the operator's Start-menu entry — that is what --register
    /// is for), and a build-output exe never claims it at all (`dotnet run` and debug
    /// builds are throwaway binaries).
    /// </summary>
    public static HealthCheck CheckHealth(
        string currentExePath, string? registeredExe, string? registeredWorkingDir,
        bool shortcutExists, bool switcherShortcutExists,
        Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        if (IsBuildOutput(currentExePath)) return Healthy;
        if (string.IsNullOrEmpty(registeredExe))
            return new(true, shortcutExists ? "App Paths entry missing" : "not registered");
        if (!fileExists(registeredExe)) return new(true, "registered exe is gone");
        if (string.IsNullOrEmpty(registeredWorkingDir) || !dirExists(registeredWorkingDir))
            return new(true, "registered working dir is gone");
        if (!shortcutExists) return new(true, "Start-menu shortcut missing");
        if (!switcherShortcutExists) return new(true, "switcher shortcut missing");
        return Healthy;
    }

    /// <summary>A bin/ or obj/ path segment — the .NET build-output convention.</summary>
    private static bool IsBuildOutput(string exePath) =>
        (Path.GetDirectoryName(exePath) ?? "")
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                     || seg.Equals("obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Startup heal: make the Start-menu entry exist and point at this exe/root, so a
    /// fresh clone is reachable from the Start menu without anyone knowing --register
    /// exists, and a moved repo fixes itself on next launch. Silent when healthy;
    /// never fatal. Gated by the shellRegistration setting.
    /// </summary>
    public static void EnsureRegistered(string root, Action<string> log)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(root)) return;

            var plan = Plan(exePath, root);
            string? registeredExe, registeredWorkingDir;
            using (var key = Registry.CurrentUser.OpenSubKey(AppPathsKey))
            {
                registeredExe = key?.GetValue("") as string;
                registeredWorkingDir = key?.GetValue("WorkingDir") as string;
            }

            var check = CheckHealth(exePath, registeredExe, registeredWorkingDir,
                File.Exists(plan.ShortcutPath), File.Exists(plan.SwitcherShortcutPath),
                File.Exists, Directory.Exists);
            if (!check.ShouldRegister) return;

            Apply(plan, log);
            log($"Shell entry: registered 'huddle' in the Start menu ({check.Reason})");
        }
        catch (Exception ex)
        {
            // Never block startup over a shortcut.
            log($"Shell entry: self-registration skipped ({ex.Message})");
        }
    }

    // ---- Executors (thin; verified by operator live smoke) ------------------------

    /// <summary>Write all three surfaces. Each executor logs its own failure and
    /// continues — a missing shortcut must not cost the App Paths entry.</summary>
    private static void Apply(RegistrationPlan plan, Action<string> log)
    {
        RegisterAumid(log);
        CreateStartMenuShortcut(plan, log);
        RegisterAppPaths(plan, log);
    }

    private static void RegisterAumid(Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AumidKey);
            key.SetValue("DisplayName", DisplayName);
        }
        catch (Exception ex) { log($"register: could not write AUMID key: {ex.Message}"); }
    }

    private static void RegisterAppPaths(RegistrationPlan plan, Action<string> log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppPathsKey);
            key.SetValue("", plan.ExePath);
            key.SetValue("WorkingDir", plan.WorkingDir);
        }
        catch (Exception ex) { log($"register: could not write App Paths key: {ex.Message}"); }
    }

    private static void CreateStartMenuShortcut(RegistrationPlan plan, Action<string> log)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        { log("register: WScript.Shell unavailable — Start-menu shortcut not created"); return; }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var shortcut = shell.CreateShortcut(plan.ShortcutPath);
            try
            {
                shortcut.TargetPath = plan.ExePath;
                shortcut.WorkingDirectory = plan.WorkingDir;
                shortcut.Description = "Claude Huddle — session orchestrator";
                shortcut.IconLocation = plan.ExePath + ",0";
                shortcut.Save();
                SetShortcutAumid(plan.ShortcutPath, Aumid);

                var switcher = shell.CreateShortcut(plan.SwitcherShortcutPath);
                try
                {
                    switcher.TargetPath = plan.ExePath;
                    switcher.Arguments = "--peek";
                    switcher.WorkingDirectory = plan.WorkingDir;
                    switcher.Description = "Huddle sessions — thumbnail switcher";
                    switcher.IconLocation = plan.ExePath + ",0";
                    switcher.Save();
                    SetShortcutAumid(plan.SwitcherShortcutPath, Aumid);
                }
                finally { Marshal.ReleaseComObject(switcher); }
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        catch (Exception ex) { log($"register: could not create shortcut: {ex.Message}"); }
        finally { Marshal.ReleaseComObject(shell); }
    }

    /// <summary>AUMID onto the .lnk via IPropertyStore — verbatim from the prototype;
    /// this is what makes pinning and Start-search treat huddle as one app.</summary>
    private static void SetShortcutAumid(string shortcutPath, string aumid)
    {
        try
        {
            var hr = SHGetPropertyStoreFromParsingName(
                shortcutPath, IntPtr.Zero, GETPROPERTYSTOREFLAGS.GPS_READWRITE,
                typeof(IPropertyStore).GUID, out var propertyStore);
            if (hr != 0 || propertyStore == null) return;
            try
            {
                var pkey = new PROPERTYKEY
                {
                    fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                    pid = 5 // System.AppUserModel.ID
                };
                var propVar = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, pwszVal = Marshal.StringToCoTaskMemUni(aumid) };
                try
                {
                    propertyStore.SetValue(ref pkey, ref propVar);
                    propertyStore.Commit();
                }
                finally { Marshal.FreeCoTaskMem(propVar.pwszVal); }
            }
            finally { Marshal.ReleaseComObject(propertyStore); }
        }
        catch { /* non-fatal: shortcut works, just without embedded AUMID */ }
    }

    #region Native interop (prototype port)

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, GETPROPERTYSTOREFLAGS flags,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    private enum GETPROPERTYSTOREFLAGS { GPS_READWRITE = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    #endregion
}
