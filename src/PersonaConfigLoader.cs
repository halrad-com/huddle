// src/PersonaConfigLoader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Huddle;

public static class PersonaConfigLoader
{
    /// <summary>Load _shared.json + &lt;persona&gt;.json from personasDir, merge, return.</summary>
    /// <remarks>Missing files yield empty configs. Malformed JSON throws.</remarks>
    public static PersonaConfig LoadAndMerge(string personasDir, string? persona)
    {
        var shared = Load(Path.Combine(personasDir, "_shared.json"));
        if (persona == null) return shared;
        var p = Load(Path.Combine(personasDir, $"{persona}.json"));
        return Merge(shared, p);
    }

    public static PersonaConfig Load(string path)
    {
        if (!File.Exists(path)) return new PersonaConfig();
        var json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<PersonaConfig>(json, JsonOpts) ?? new PersonaConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Malformed persona JSON: {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Shallow merge: scalars/arrays from b override a; objects merge per-key.</summary>
    public static PersonaConfig Merge(PersonaConfig a, PersonaConfig b) => new PersonaConfig
    {
        Model                = b.Model                ?? a.Model,
        Effort               = b.Effort               ?? a.Effort,
        Bare                 = b.Bare                 ?? a.Bare,
        PluginDirs           = b.PluginDirs           ?? a.PluginDirs,
        DisableSlashCommands = b.DisableSlashCommands ?? a.DisableSlashCommands,
        Tools                = b.Tools                ?? a.Tools,
        AllowedTools         = b.AllowedTools         ?? a.AllowedTools,
        DisallowedTools      = b.DisallowedTools      ?? a.DisallowedTools,
        McpServers           = MergeDict(a.McpServers, b.McpServers),
        StrictMcp            = b.StrictMcp            ?? a.StrictMcp,
        Agents               = MergeDict(a.Agents, b.Agents),
        PermissionMode       = b.PermissionMode       ?? a.PermissionMode,
        AddDirs              = b.AddDirs              ?? a.AddDirs,
        SettingsOverride     = MergeDict(a.SettingsOverride, b.SettingsOverride),
    };

    static Dictionary<string, object>? MergeDict(Dictionary<string, object>? a, Dictionary<string, object>? b)
    {
        if (a == null && b == null) return null;
        var result = new Dictionary<string, object>(a ?? new());
        if (b != null) foreach (var kv in b) result[kv.Key] = kv.Value;
        return result;
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
