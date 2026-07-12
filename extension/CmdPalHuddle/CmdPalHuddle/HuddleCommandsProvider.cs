// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using CmdPalHuddle.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle;

public partial class HuddleCommandsProvider : CommandProvider
{
    private readonly HuddleSettings _settings = new();
    private readonly HuddleClient _client;

    public HuddleCommandsProvider()
    {
        _client = new HuddleClient(_settings);
        DisplayName = "Huddle";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
    }

    public override ICommandItem[] TopLevelCommands()
    {
        var alive = _client.IsHuddleAlive();
        var items = new List<ICommandItem>();

        if (!alive)
        {
            items.Add(new CommandItem(new NoOpCommand())
            {
                Title = "Huddle is not running",
                Subtitle = "Start it from your terminal-shell shortcut, then reload the palette",
            });
        }

        // Read views — always available; render gracefully when huddle is down.
        items.Add(new CommandItem(new StatusPage(_client)) { Title = "Huddle: Status" });
        items.Add(new CommandItem(new ReposPage(_client)) { Title = "Huddle: Repos" });
        items.Add(new CommandItem(new PersonasPage(_client)) { Title = "Huddle: Personas" });
        items.Add(new CommandItem(new ConflictsPage(_client)) { Title = "Huddle: Conflicts" });

        // Form-driven write verbs — gated on liveness AND showWrites.
        if (alive && _settings.ShowWriteCommands)
        {
            items.Add(new CommandItem(new DirectFormPage(_client)) { Title = "Huddle: Direct" });
            items.Add(new CommandItem(new StartSessionFormPage(_client)) { Title = "Huddle: Start session" });
            items.Add(new CommandItem(new BroadcastFormPage(_client)) { Title = "Huddle: Broadcast" });
        }

        // Launch — visible always (especially useful when not alive).
        items.Add(new CommandItem(new LaunchCommand(_settings)) { Title = "Huddle: Launch" });

        // Setup — first-run helper. Always visible; the form skips anything already set up.
        items.Add(new CommandItem(new SetupLauncherFormPage(_client)) { Title = "Huddle: Setup launcher" });

        return items.ToArray();
    }
}
