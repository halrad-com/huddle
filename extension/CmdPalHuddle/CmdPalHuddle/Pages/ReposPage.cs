// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using CmdPalHuddle.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class ReposPage : ListPage
{
    private readonly HuddleClient _client;

    public ReposPage(HuddleClient client)
    {
        _client = client;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Repos";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        var repos = _client.GetRepos();
        if (repos.Count == 0)
        {
            return [
                new ListItem(new NoOpCommand())
                {
                    Title = "No repos found",
                    Subtitle = "huddle.json was not found or has no sessions defined",
                }
            ];
        }

        return repos
            .Select(r => (IListItem)new ListItem(new RepoDetailPage(_client, r))
            {
                Title = r.Aliases.Count > 0
                    ? $"{r.Name}  ·  {string.Join(", ", r.Aliases)}"
                    : r.Name,
                Subtitle = r.Purpose ?? r.Root,
            })
            .ToArray();
    }
}
