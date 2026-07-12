// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class StatusPage : ListPage
{
    private readonly HuddleClient _client;

    public StatusPage(HuddleClient client)
    {
        _client = client;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Status";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        var sessions = _client.GetSessions();
        if (sessions.Count == 0)
        {
            return [
                new ListItem(new NoOpCommand())
                {
                    Title = "No sessions",
                    Subtitle = "Use 'Huddle: Start session' to launch one",
                }
            ];
        }

        return sessions.Select(s =>
        {
            var statusGlyph = s.Status switch
            {
                "running" => "[run]",
                "idle" => "[idle]",
                _ => "[?]",
            };
            var subtitle = s.StartedAt is { } when
                ? $"{s.Status}  ·  last activity {when.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : s.Status;

            // Capture for closure; struct copies are fine.
            var safeName = s.SafeName;
            var instanceId = s.InstanceId;

            var stopCmd = new AnonymousCommand(() =>
            {
                var stopBody = new JsonObject { ["instanceId"] = instanceId };
                _client.WriteCommand("_huddle", "stop-session", "command", stopBody);
            })
            {
                Name = "Stop",
                Result = CommandResult.GoHome(),
            };

            // Row click → SendMessageFormPage targeting this session.
            return (IListItem)new ListItem(new SendMessageFormPage(_client, safeName, instanceId))
            {
                Title = $"{statusGlyph} {instanceId}",
                Subtitle = subtitle,
                MoreCommands =
                [
                    new CommandContextItem(stopCmd) { Title = "Stop session" },
                ],
            };
        }).ToArray();
    }
}
