namespace CmdPalHuddle.Models;

using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class IpcEnvelope
{
    [JsonPropertyName("from")]      public string From { get; init; } = "cmdpal:huddle";
    [JsonPropertyName("to")]        public string To { get; init; } = "_huddle";
    [JsonPropertyName("timestamp")] public string Timestamp { get; init; } = DateTimeOffset.UtcNow.ToString("o");
    [JsonPropertyName("type")]      public string Type { get; init; } = "command";
    [JsonPropertyName("subject")]   public string Subject { get; init; } = "";
    [JsonPropertyName("body")]      public JsonNode? Body { get; init; }
}
