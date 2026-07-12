// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class SetupLauncherFormPage : ContentPage
{
    private readonly SetupLauncherForm _form;

    public SetupLauncherFormPage(HuddleClient client)
    {
        _form = new SetupLauncherForm(client);
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Setup launcher";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class SetupLauncherForm : FormContent
{
    private readonly HuddleClient _client;

    public SetupLauncherForm(HuddleClient client)
    {
        _client = client;

        var detectedExe = TryFindHuddleExe(client) ?? string.Empty;
        var defaultProfile = "claude-huddle";

        TemplateJson = $$"""
{
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": [
        {
            "type": "TextBlock",
            "size": "medium",
            "weight": "bolder",
            "text": "Set up Windows Terminal profile and Claude statusline",
            "wrap": true,
            "style": "heading"
        },
        {
            "type": "TextBlock",
            "text": "Adds a Windows Terminal profile that launches huddle.exe and installs the [repo:persona] statusline into ~/.claude/. Existing pieces are left alone.",
            "wrap": true,
            "isSubtle": true
        },
        {
            "type": "Input.Text",
            "id": "huddleExe",
            "label": "Path to huddle.exe",
            "value": "{{Esc(detectedExe)}}",
            "isRequired": true,
            "errorMessage": "huddle.exe path is required",
            "placeholder": "e.g. C:\\Users\\you\\source\\repos\\seatbelt\\publish\\huddle.exe"
        },
        {
            "type": "Input.Text",
            "id": "profileName",
            "label": "Windows Terminal profile name",
            "value": "{{Esc(defaultProfile)}}",
            "isRequired": true,
            "errorMessage": "Profile name is required"
        }
    ],
    "actions": [
        { "type": "Action.Submit", "title": "Set up missing pieces" }
    ]
}
""";
    }

    public override CommandResult SubmitForm(string payload)
    {
        var input = JsonNode.Parse(payload)?.AsObject();
        var huddleExe = input?["huddleExe"]?.ToString() ?? string.Empty;
        var profileName = input?["profileName"]?.ToString() ?? "claude-huddle";

        if (string.IsNullOrWhiteSpace(huddleExe) || !File.Exists(huddleExe))
        {
            new ToastStatusMessage("huddle.exe path is missing or not found").Show();
            return CommandResult.KeepOpen();
        }

        var actions = new List<string>();

        try
        {
            var wtAdded = TryAddWtProfile(huddleExe, profileName);
            actions.Add(wtAdded ? $"Added WT profile '{profileName}'" : $"WT profile '{profileName}' already present (or WT not installed) — skipped");

            var statuslineCopied = TryInstallStatusline();
            actions.Add(statuslineCopied ? "Installed ~/.claude/statusline.ps1" : "Statusline already present or source script not found — skipped");

            var settingsUpdated = TryWireStatuslineSetting();
            actions.Add(settingsUpdated ? "Wired statusLine into ~/.claude/settings.json" : "statusLine setting already configured — skipped");
        }
        catch (Exception ex)
        {
            new ToastStatusMessage("Setup failed: " + ex.Message).Show();
            return CommandResult.KeepOpen();
        }

        new ToastStatusMessage("Setup complete: " + string.Join(" · ", actions)).Show();
        return CommandResult.GoHome();
    }

    private static string? TryFindHuddleExe(HuddleClient client)
    {
        // 1. Running process
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("huddle"))
            {
                using (p)
                {
                    try { return p.MainModule?.FileName; }
                    catch { /* try next */ }
                }
            }
        }
        catch { /* fall through */ }

        // 2. Walk known build outputs under huddle root
        var root = client.GetHuddleRoot();
        if (!string.IsNullOrEmpty(root))
        {
            foreach (var rel in new[] { "publish/huddle.exe", "publish-staging/huddle.exe", "src/bin/Release/net8.0/win-x64/publish/huddle.exe" })
            {
                var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    private static bool TryAddWtProfile(string huddleExe, string profileName)
    {
        var settingsPath = Environment.ExpandEnvironmentVariables(
            @"%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        if (!File.Exists(settingsPath)) return false;

        var doc = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();

        // WT may store profiles either as a bare array on "profiles" or under "profiles.list".
        JsonArray? list = doc["profiles"]?["list"] as JsonArray ?? doc["profiles"] as JsonArray;
        if (list is null)
        {
            // Establish the canonical "profiles": { "list": [] } shape.
            var profilesObj = new JsonObject();
            list = new JsonArray();
            profilesObj["list"] = list;
            doc["profiles"] = profilesObj;
        }

        // Skip if a profile with this name already exists.
        foreach (var p in list)
        {
            if (string.Equals(p?["name"]?.GetValue<string>(), profileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var startDir = Path.GetDirectoryName(huddleExe) ?? string.Empty;
        var newProfile = new JsonObject
        {
            ["guid"] = "{" + Guid.NewGuid().ToString() + "}",
            ["name"] = profileName,
            ["commandline"] = huddleExe,
            ["startingDirectory"] = startDir,
            ["hidden"] = false,
        };
        list.Add(newProfile);

        AtomicWrite(settingsPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static bool TryInstallStatusline()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dest = Path.Combine(userProfile, ".claude", "statusline.ps1");
        if (File.Exists(dest)) return false;

        // Source: walk up from the running extension's location until we find scripts/statusline.ps1
        // OR look under the huddle root.
        var source = FindStatuslineSource();
        if (source is null) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: false);
        return true;
    }

    private static string? FindStatuslineSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "scripts", "statusline.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        return null;
    }

    private static bool TryWireStatuslineSetting()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settingsPath = Path.Combine(userProfile, ".claude", "settings.json");
        var statuslinePath = Path.Combine(userProfile, ".claude", "statusline.ps1").Replace('\\', '/');

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        JsonObject doc;
        if (File.Exists(settingsPath))
        {
            doc = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
            if (doc["statusLine"] is JsonObject existing
                && existing["command"]?.GetValue<string>()?.Contains("statusline.ps1", StringComparison.OrdinalIgnoreCase) == true)
            {
                return false; // already configured
            }
        }
        else
        {
            doc = new JsonObject();
        }

        doc["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {statuslinePath}",
        };

        AtomicWrite(settingsPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
