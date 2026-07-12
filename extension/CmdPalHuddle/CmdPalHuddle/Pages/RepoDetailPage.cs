// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using CmdPalHuddle.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class RepoDetailPage : ContentPage
{
    private readonly HuddleClient _client;
    private readonly RepoInfo _repo;

    public RepoDetailPage(HuddleClient client, RepoInfo repo)
    {
        _client = client;
        _repo = repo;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = $"Huddle: {repo.Name}";
        Name = "Open";
    }

    public override IContent[] GetContent()
    {
        var lines = new List<string>
        {
            $"# {_repo.Name}",
            string.Empty,
            $"**Root:** `{_repo.Root}`",
        };

        if (_repo.Aliases.Count > 0)
        {
            lines.Add($"**Aliases:** {string.Join(", ", _repo.Aliases)}");
        }

        if (!string.IsNullOrEmpty(_repo.Purpose))
        {
            lines.Add(string.Empty);
            lines.Add(_repo.Purpose!);
        }

        return [new MarkdownContent(string.Join("\n", lines))];
    }

    // Task 19 will add a "Start a session here" action once StartSessionFormPage exists —
    // the attachment shape (context items vs. ListItem.MoreCommands on the row that
    // navigates here) depends on the Toolkit's ContentPage surface, which doesn't
    // expose GetCommands(); deferred to Task 19.
}
