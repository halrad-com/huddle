// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class ConflictsPage : ListPage
{
    private readonly HuddleClient _client;

    public ConflictsPage(HuddleClient client)
    {
        _client = client;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Conflicts";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        var conflicts = _client.GetConflicts();
        if (conflicts.Count == 0)
        {
            return [
                new ListItem(new NoOpCommand())
                {
                    Title = "No conflicts",
                    Subtitle = "No file overlaps across active claims",
                }
            ];
        }

        return conflicts.Select(c =>
            (IListItem)new ListItem(new NoOpCommand())
            {
                Title = c.FilePath,
                Subtitle = $"{c.Source}  ·  held by: {string.Join(", ", c.Holders)}",
            }).ToArray();
    }
}
