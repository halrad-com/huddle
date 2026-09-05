using Huddle;
using Xunit;

namespace HuddleTests;

// Windows shell entry (spec 2026-08-31-shell-registration-design.md §2).
// Pure plan logic; the COM/registry executors are thin and operator-smoked.
public class ShellRegistrationTests
{
    [Fact]
    public void Plan_targets_running_exe_with_repo_root_as_working_dir()
    {
        var p = ShellRegistration.Plan(@"C:\repos\huddle\publish\huddle.exe", @"C:\repos\huddle");
        Assert.Equal(@"C:\repos\huddle\publish\huddle.exe", p.ExePath);
        Assert.Equal(@"C:\repos\huddle", p.WorkingDir);
        Assert.EndsWith(@"\Programs\huddle.lnk", p.ShortcutPath);
        Assert.Contains(@"Start Menu", p.ShortcutPath);
        // The pinnable one. This literal is the user-visible contract: an operator pins
        // "Huddle Sessions" to the taskbar, and a rename here breaks that pin silently.
        // Its `--peek` argument is set in Apply, not Plan, so the argument itself stays
        // operator-smoked by design; the filename does not have to be.
        Assert.EndsWith(@"\Programs\Huddle Sessions.lnk", p.SwitcherShortcutPath);
    }

    [Fact]
    public void Plan_registry_paths_are_per_user_and_stable()
    {
        var p = ShellRegistration.Plan(@"C:\x\huddle.exe", @"C:\x");
        Assert.Equal(@"Software\Classes\AppUserModelId\" + ShellRegistration.Aumid, p.AumidKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\App Paths\huddle.exe", p.AppPathsKeyPath);
        Assert.Equal("HALRAD.Huddle", ShellRegistration.Aumid);
    }
}

public class ConfigPathResolverFallbackTests : IDisposable
{
    private readonly string _dir;
    public ConfigPathResolverFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "huddle-cpr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Registered_root_is_used_only_when_cwd_has_no_config()
    {
        var root = Path.Combine(_dir, "registered");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "huddle.json"), "{}");

        // cwd empty -> falls through to the registered root's huddle.json.
        var resolved = ConfigPathResolver.Resolve(Array.Empty<string>(), _dir, () => root);
        Assert.Equal(Path.Combine(root, "huddle.json"), resolved);

        // cwd HAS a config -> registered root must not hijack it.
        File.WriteAllText(Path.Combine(_dir, "huddle.json"), "{}");
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve(Array.Empty<string>(), _dir, () => root));
    }

    [Fact]
    public void Missing_or_configless_registered_root_changes_nothing()
    {
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve(Array.Empty<string>(), _dir, () => null));
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal("huddle.json", ConfigPathResolver.Resolve(Array.Empty<string>(), _dir, () => empty));
    }

    [Fact]
    public void Explicit_config_flag_still_wins_over_everything()
    {
        var root = Path.Combine(_dir, "registered");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "huddle.json"), "{}");
        Assert.Equal(@"X:\mine.json",
            ConfigPathResolver.Resolve(new[] { "--config", @"X:\mine.json" }, _dir, () => root));
    }
}

// Self-registration health check (the startup heal). Pure decision; the registry
// reads and COM writes around it are the same thin executors --register uses.
public class ShellRegistrationHealthTests
{
    private const string Exe = @"C:\repos\huddle\publish\huddle.exe";
    private const string Root = @"C:\repos\huddle";

    private static ShellRegistration.HealthCheck Check(
        string? registeredExe, string? registeredWorkingDir, bool shortcutExists,
        string currentExe = Exe, string[]? existingFiles = null, string[]? existingDirs = null)
        => ShellRegistration.CheckHealth(
            currentExe, registeredExe, registeredWorkingDir, shortcutExists,
            switcherShortcutExists: true,
            p => (existingFiles ?? new[] { Exe }).Contains(p, StringComparer.OrdinalIgnoreCase),
            p => (existingDirs ?? new[] { Root }).Contains(p, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Nothing_registered_registers()
    {
        var c = Check(null, null, shortcutExists: false);
        Assert.True(c.ShouldRegister);
        Assert.Equal("not registered", c.Reason);
    }

    [Fact]
    public void Healthy_registration_does_nothing()
        => Assert.False(Check(Exe, Root, shortcutExists: true).ShouldRegister);

    [Fact]
    public void Registered_exe_gone_heals()
    {
        // The repo moved: App Paths still points at the old location.
        var c = Check(@"C:\old\publish\huddle.exe", Root, shortcutExists: true);
        Assert.True(c.ShouldRegister);
        Assert.Equal("registered exe is gone", c.Reason);
    }

    [Fact]
    public void Registered_working_dir_gone_heals()
    {
        var c = Check(Exe, @"C:\old", shortcutExists: true);
        Assert.True(c.ShouldRegister);
        Assert.Equal("registered working dir is gone", c.Reason);
    }

    [Fact]
    public void Deleted_shortcut_is_restored()
    {
        var c = Check(Exe, Root, shortcutExists: false);
        Assert.True(c.ShouldRegister);
        Assert.Equal("Start-menu shortcut missing", c.Reason);
    }

    [Fact]
    public void Missing_app_paths_with_a_shortcut_still_registers()
    {
        var c = Check(null, null, shortcutExists: true);
        Assert.True(c.ShouldRegister);
        Assert.Equal("App Paths entry missing", c.Reason);
    }

    [Fact]
    public void A_healthy_registration_pointing_elsewhere_is_never_hijacked()
    {
        // A second clone must not silently steal the Start-menu entry from the
        // first. --register remains the explicit way to repoint it.
        var other = @"D:\clone\publish\huddle.exe";
        var c = Check(other, @"D:\clone", shortcutExists: true,
            existingFiles: new[] { Exe, other }, existingDirs: new[] { Root, @"D:\clone" });
        Assert.False(c.ShouldRegister);
    }

    // The second shortcut is the operator's actual route in: it is what gets pinned to
    // the taskbar. A missing one has to heal exactly like a missing first one, or an
    // upgrade from a build that predates it never grows the button.
    [Fact]
    public void A_missing_switcher_shortcut_is_healed()
    {
        var health = ShellRegistration.CheckHealth(
            currentExePath: @"C:\repos\myapp\publish\huddle.exe",
            registeredExe: @"C:\repos\myapp\publish\huddle.exe",
            registeredWorkingDir: @"C:\repos\myapp",
            shortcutExists: true,
            switcherShortcutExists: false,
            fileExists: _ => true,
            dirExists: _ => true);

        Assert.True(health.ShouldRegister);
        Assert.Contains("switcher", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_both_shortcuts_present_nothing_is_rewritten()
    {
        var health = ShellRegistration.CheckHealth(
            currentExePath: @"C:\repos\myapp\publish\huddle.exe",
            registeredExe: @"C:\repos\myapp\publish\huddle.exe",
            registeredWorkingDir: @"C:\repos\myapp",
            shortcutExists: true,
            switcherShortcutExists: true,
            fileExists: _ => true,
            dirExists: _ => true);

        Assert.False(health.ShouldRegister);
    }

    [Theory]
    [InlineData(@"C:\repos\huddle\src\bin\Debug\net8.0\huddle.exe")]
    [InlineData(@"C:\repos\huddle\src\obj\Release\huddle.exe")]
    public void A_build_output_exe_never_claims_the_shell_entry(string buildExe)
    {
        // dotnet run / a debug build must not repoint the operator's Start menu at
        // a throwaway binary. Only a published exe self-registers.
        var c = Check(null, null, shortcutExists: false, currentExe: buildExe);
        Assert.False(c.ShouldRegister);
    }
}
