// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPalHuddle.Pages;

internal sealed partial class StartSessionFormPage : ContentPage
{
    private readonly StartSessionForm _form;

    public StartSessionFormPage(HuddleClient client, string? prefilledRepo = null)
    {
        _form = new StartSessionForm(client, prefilledRepo);
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Huddle: Start session";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class StartSessionForm : FormContent
{
    private readonly HuddleClient _client;

    public StartSessionForm(HuddleClient client, string? prefilledRepo)
    {
        _client = client;

        var repos = client.GetRepos();
        var personas = client.GetPersonas();
        var firstRepo = prefilledRepo ?? repos.FirstOrDefault()?.Name ?? string.Empty;
        var firstPersona = personas.FirstOrDefault()?.Name ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("""
{
    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": [
        {
            "type": "TextBlock",
            "size": "medium",
            "weight": "bolder",
            "text": "Start a Claude Code session",
            "wrap": true,
            "style": "heading"
        },
""");
        sb.Append("""
        {
            "type": "Input.ChoiceSet",
            "id": "repo",
            "label": "Repo",
            "style": "compact",
            "isRequired": true,
""");
        sb.Append($"            \"value\": \"{Esc(firstRepo)}\",\n");
        sb.Append("            \"choices\": [");
        sb.Append(string.Join(",", repos.Select(r =>
            $"\n                {{ \"title\": \"{Esc(r.Name)}\", \"value\": \"{Esc(r.Name)}\" }}")));
        sb.Append("\n            ]\n        },\n");

        sb.Append("""
        {
            "type": "Input.ChoiceSet",
            "id": "persona",
            "label": "Persona",
            "style": "compact",
            "isRequired": true,
""");
        sb.Append($"            \"value\": \"{Esc(firstPersona)}\",\n");
        sb.Append("            \"choices\": [");
        sb.Append(string.Join(",", personas.Select(p =>
            $"\n                {{ \"title\": \"{Esc(p.Name)}\", \"value\": \"{Esc(p.Name)}\" }}")));
        sb.Append("\n            ]\n        },\n");

        sb.Append("""
        {
            "type": "Input.Text",
            "id": "prompt",
            "label": "Opening prompt (optional)",
            "isMultiline": true,
            "placeholder": "e.g. continue where you left off"
        }
    ],
    "actions": [
        { "type": "Action.Submit", "title": "Start session" }
    ]
}
""");
        TemplateJson = sb.ToString();
    }

    public override CommandResult SubmitForm(string payload)
    {
        var input = JsonNode.Parse(payload)?.AsObject();
        var body = new JsonObject
        {
            ["repo"] = input?["repo"]?.ToString() ?? string.Empty,
            ["persona"] = input?["persona"]?.ToString() ?? string.Empty,
            ["prompt"] = input?["prompt"]?.ToString() ?? string.Empty,
        };
        var fileName = _client.WriteCommand("_huddle", "start-session", "command", body);
        var toast = new ToastStatusMessage(fileName is null
            ? "Failed to write — check huddle is running and root is set"
            : $"Sent: {fileName}");
        toast.Show();
        return fileName is null ? CommandResult.KeepOpen() : CommandResult.GoHome();
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
