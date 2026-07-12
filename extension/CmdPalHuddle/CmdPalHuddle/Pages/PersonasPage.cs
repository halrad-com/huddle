// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class PersonasPage : ListPage
{
    private readonly HuddleClient _client;

    public PersonasPage(HuddleClient client)
    {
        _client = client;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Personas";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        var personas = _client.GetPersonas();
        if (personas.Count == 0)
        {
            return [
                new ListItem(new NoOpCommand())
                {
                    Title = "No personas found",
                    Subtitle = "personas/ directory not found or empty",
                }
            ];
        }

        return personas
            .Select(p => (IListItem)new ListItem(new NoOpCommand())
            {
                Title = p.Name,
                Subtitle = p.Subtitle ?? string.Empty,
            })
            .ToArray();
    }
}
