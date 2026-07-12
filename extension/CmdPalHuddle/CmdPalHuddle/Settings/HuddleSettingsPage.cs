// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Settings;

/// <summary>
/// Settings page exposing the three persisted Huddle fields: HuddleRoot, LaunchCommand,
/// ShowWriteCommands. Persistence is the existing <see cref="HuddleSettings"/> model
/// (Windows.Storage.ApplicationData.Current.LocalSettings) — the Toolkit's <see cref="Setting{T}"/>
/// instances act as the UI surface, and the SettingsChanged event syncs values back into
/// HuddleSettings so the rest of the extension reads through one source of truth.
/// </summary>
internal sealed partial class HuddleSettingsPage : ContentPage
{
    private readonly HuddleSettings _settings;
    private readonly TextSetting _huddleRoot;
    private readonly TextSetting _launchCommand;
    private readonly ToggleSetting _showWriteCommands;
    private readonly global::Microsoft.CommandPalette.Extensions.Toolkit.Settings _form;

    public HuddleSettingsPage(HuddleSettings settings)
    {
        _settings = settings;

        Title = "Huddle Settings";
        Name = "Open";
        Icon = new IconInfo(""); // Settings gear glyph

        _huddleRoot = new TextSetting(
            key: "HuddleRoot",
            label: "Huddle root path",
            description: "Directory containing huddle.json. Auto-detected from a running huddle.exe when blank.",
            defaultValue: _settings.HuddleRoot);

        _launchCommand = new TextSetting(
            key: "LaunchCommand",
            label: "Launch command",
            description: "Used by the Huddle: Launch action. Example: wt.exe -p \"Claude Huddle\"",
            defaultValue: _settings.LaunchCommand);

        _showWriteCommands = new ToggleSetting(
            key: "ShowWriteCommands",
            label: "Show write commands",
            description: "When off, only read-only commands appear in the palette.",
            defaultValue: _settings.ShowWriteCommands);

        _form = new global::Microsoft.CommandPalette.Extensions.Toolkit.Settings();
        _form.Add(_huddleRoot);
        _form.Add(_launchCommand);
        _form.Add(_showWriteCommands);

        _form.SettingsChanged += OnSettingsChanged;
    }

    public override IContent[] GetContent() => _form.ToContent();

    private void OnSettingsChanged(object sender, global::Microsoft.CommandPalette.Extensions.Toolkit.Settings args)
    {
        // Push UI values into the LocalSettings-backed model so HuddleClient and friends
        // read a single source of truth.
        _settings.HuddleRoot = _huddleRoot.Value ?? string.Empty;
        _settings.LaunchCommand = _launchCommand.Value ?? string.Empty;
        _settings.ShowWriteCommands = _showWriteCommands.Value;
    }
}
