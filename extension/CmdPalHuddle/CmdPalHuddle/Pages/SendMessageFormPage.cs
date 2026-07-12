// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class SendMessageFormPage : ContentPage
{
    private readonly SendMessageForm _form;

    public SendMessageFormPage(HuddleClient client, string targetSafeName, string targetInstanceId)
    {
        _form = new SendMessageForm(client, targetSafeName, targetInstanceId);
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = $"Send to {targetInstanceId}";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class SendMessageForm : FormContent
{
    private readonly HuddleClient _client;
    private readonly string _targetSafeName;
    private readonly string _targetInstanceId;

    public SendMessageForm(HuddleClient client, string targetSafeName, string targetInstanceId)
    {
        _client = client;
        _targetSafeName = targetSafeName;
        _targetInstanceId = targetInstanceId;
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
            "text": "Send to {{Esc(targetInstanceId)}}",
            "wrap": true,
            "style": "heading"
        },
        {
            "type": "Input.Text",
            "id": "subject",
            "label": "Subject",
            "isRequired": true,
            "errorMessage": "Subject is required"
        },
        {
            "type": "Input.Text",
            "id": "body",
            "label": "Body",
            "isMultiline": true
        }
    ],
    "actions": [
        { "type": "Action.Submit", "title": "Send" }
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

        var bodyNode = new JsonObject { ["text"] = body };
        var fileName = _client.WriteCommand(_targetSafeName, subject, "info", bodyNode);
        var toast = new ToastStatusMessage(fileName is null
            ? "Failed to write"
            : $"Sent to {_targetInstanceId}");
        toast.Show();
        return fileName is null ? CommandResult.KeepOpen() : CommandResult.GoHome();
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
