namespace CmdPalHuddle;

using System;
using System.IO;
using System.Text.Json.Nodes;
using Windows.Storage;

public sealed class HuddleSettings
{
    private const string KeyHuddleRoot   = "HuddleRoot";
    private const string KeyLaunchCmd    = "LaunchCmd";
    private const string KeyShowWrites   = "ShowWrites";

    private readonly ApplicationDataContainer _store = ApplicationData.Current.LocalSettings;

    public string HuddleRoot
    {
        get => _store.Values[KeyHuddleRoot] as string ?? string.Empty;
        set => _store.Values[KeyHuddleRoot] = value ?? string.Empty;
    }

    public string LaunchCommand
    {
        get => _store.Values[KeyLaunchCmd] as string ?? DefaultLaunchCommand();
        set => _store.Values[KeyLaunchCmd] = value ?? string.Empty;
    }

    public bool ShowWriteCommands
    {
        get => _store.Values[KeyShowWrites] is bool b ? b : true;
        set => _store.Values[KeyShowWrites] = value;
    }

    private static string DefaultLaunchCommand()
    {
        var wt = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe");
        if (File.Exists(wt))
        {
            // Scan WT's settings.json for a profile that runs huddle. Match by
            // either profile name OR commandline containing "huddle". Whatever
            // the user actually named their profile is what we should use.
            var profile = FindWindowsTerminalHuddleProfile();
            if (!string.IsNullOrEmpty(profile))
            {
                return $@"wt.exe -p ""{profile}""";
            }
        }

        // Fallback: huddle.exe directly (works if it's on PATH).
        return "huddle.exe";
    }

    private static string? FindWindowsTerminalHuddleProfile()
    {
        var settingsPath = Environment.ExpandEnvironmentVariables(
            @"%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        if (!File.Exists(settingsPath)) return null;

        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(settingsPath));
            var profiles = doc?["profiles"]?["list"] as JsonArray
                ?? doc?["profiles"] as JsonArray;
            if (profiles is null) return null;

            foreach (var p in profiles)
            {
                if (p is null) continue;
                var name = p["name"]?.GetValue<string>() ?? string.Empty;
                var cmd = p["commandline"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                var matches =
                    name.Contains("huddle", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("huddle", StringComparison.OrdinalIgnoreCase);
                if (matches) return name;
            }
        }
        catch { /* swallow — fall back to huddle.exe */ }

        return null;
    }
}
