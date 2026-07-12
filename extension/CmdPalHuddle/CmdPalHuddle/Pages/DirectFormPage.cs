// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class DirectFormPage : ContentPage
{
    private readonly DirectForm _form;

    public DirectFormPage(HuddleClient client)
    {
        _form = new DirectForm(client);
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Direct";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class DirectForm : FormContent
{
    private readonly HuddleClient _client;

    public DirectForm(HuddleClient client)
    {
        _client = client;
        TemplateJson = """
{
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": [
        {
            "type": "TextBlock",
            "size": "medium",
            "weight": "bolder",
            "text": "Hand a task to architect",
            "wrap": true,
            "style": "heading"
        },
        {
            "type": "TextBlock",
            "text": "Architect plans and dispatches via dispatch-batch (autoFire).",
            "wrap": true,
            "isSubtle": true
        },
        {
            "type": "Input.Text",
            "id": "task",
            "label": "Task description",
            "isMultiline": true,
            "isRequired": true,
            "errorMessage": "Task is required",
            "placeholder": "e.g. refactor the auth flow to use the new token service"
        }
    ],
    "actions": [
        { "type": "Action.Submit", "title": "Send to architect" }
    ]
}
""";
    }

    public override CommandResult SubmitForm(string payload)
    {
        var input = JsonNode.Parse(payload)?.AsObject();
        var task = input?["task"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(task))
        {
            return CommandResult.KeepOpen();
        }

        var body = new JsonObject
        {
            ["task"] = task,
            ["autoFire"] = true,
        };
        var fileName = _client.WriteCommand("huddle_architect", "direct-task", "task", body);
        var toast = new ToastStatusMessage(fileName is null
            ? "Failed to write — check huddle is running and root is set"
            : $"Sent to architect: {fileName}");
        toast.Show();
        return fileName is null ? CommandResult.KeepOpen() : CommandResult.GoHome();
    }
}
