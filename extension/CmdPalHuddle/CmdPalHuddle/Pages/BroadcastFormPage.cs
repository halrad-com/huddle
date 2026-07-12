// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class BroadcastFormPage : ContentPage
{
    private readonly BroadcastForm _form;

    public BroadcastFormPage(HuddleClient client)
    {
        _form = new BroadcastForm(client);
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Broadcast";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class BroadcastForm : FormContent
{
    private readonly HuddleClient _client;

    public BroadcastForm(HuddleClient client)
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
            "text": "Broadcast to every live session",
            "wrap": true,
            "style": "heading"
        },
        {
            "type": "Input.Text",
            "id": "subject",
            "label": "Subject",
            "isRequired": true,
            "errorMessage": "Subject is required",
            "placeholder": "e.g. context update"
        },
        {
            "type": "Input.Text",
            "id": "body",
            "label": "Body",
            "isMultiline": true,
            "placeholder": "Optional message body"
        }
    ],
    "actions": [
        { "type": "Action.Submit", "title": "Broadcast" }
    ]
}
""";
    }

    public override CommandResult SubmitForm(string payload)
    {
        var input = JsonNode.Parse(payload)?.AsObject();
        var subject = input?["subject"]?.ToString() ?? string.Empty;
        var body = input?["body"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return CommandResult.KeepOpen();
        }

        var bodyNode = new JsonObject
        {
            ["subject"] = subject,
            ["body"] = body,
            ["type"] = "info",
            ["targets"] = "all",
        };
        var fileName = _client.WriteCommand("_huddle", "broadcast", "command", bodyNode);
        var toast = new ToastStatusMessage(fileName is null
            ? "Failed to write — check huddle is running and root is set"
            : "Broadcast queued");
        toast.Show();
        return fileName is null ? CommandResult.KeepOpen() : CommandResult.GoHome();
    }
}
