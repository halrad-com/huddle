using System.Text.Json.Serialization;

namespace Huddle;

public sealed class PersonaConfig
{
    [JsonPropertyName("model")]              public string? Model { get; set; }
    [JsonPropertyName("effort")]             public string? Effort { get; set; }
    [JsonPropertyName("bare")]               public bool? Bare { get; set; }
    [JsonPropertyName("pluginDirs")]         public List<string>? PluginDirs { get; set; }
    [JsonPropertyName("disableSlashCommands")] public bool? DisableSlashCommands { get; set; }

    [JsonPropertyName("tools")]              public List<string>? Tools { get; set; }
    [JsonPropertyName("allowedTools")]       public List<string>? AllowedTools { get; set; }
    [JsonPropertyName("disallowedTools")]    public List<string>? DisallowedTools { get; set; }

    [JsonPropertyName("mcpServers")]         public Dictionary<string, object>? McpServers { get; set; }
    [JsonPropertyName("strictMcp")]          public bool? StrictMcp { get; set; }

    [JsonPropertyName("agents")]             public Dictionary<string, object>? Agents { get; set; }

    [JsonPropertyName("permissionMode")]     public string? PermissionMode { get; set; }
    [JsonPropertyName("addDirs")]            public List<string>? AddDirs { get; set; }

    [JsonPropertyName("settingsOverride")]   public Dictionary<string, object>? SettingsOverride { get; set; }
}
