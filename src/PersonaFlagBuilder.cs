// src/PersonaFlagBuilder.cs
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Huddle;

public sealed record FlagBuildResult(string Args, List<string> TempFiles);

public static class PersonaFlagBuilder
{
    /// <summary>Materialize a PersonaConfig as Claude CLI flags. Writes temp files for
    /// mcp-config and settings-override into <paramref name="sessionLogDir"/>. Returns
    /// the args string and the list of temp file paths created (for cleanup on stop).</summary>
    public static FlagBuildResult Build(PersonaConfig cfg, string sessionLogDir, string safePathName)
    {
        var sb = new StringBuilder();
        var temps = new List<string>();

        if (!string.IsNullOrEmpty(cfg.Model))
            sb.Append($" --model {cfg.Model}");

        if (!string.IsNullOrEmpty(cfg.Effort))
            sb.Append($" --effort {cfg.Effort}");

        if (cfg.Bare == true)
            sb.Append(" --bare");

        if (cfg.PluginDirs != null)
            foreach (var d in cfg.PluginDirs)
                sb.Append($" --plugin-dir \"{SessionManager.EscapeForCmdQuoted(d)}\"");

        if (cfg.DisableSlashCommands == true)
            sb.Append(" --disable-slash-commands");

        if (cfg.Tools != null)
            sb.Append($" --tools \"{string.Join(",", cfg.Tools)}\"");

        if (cfg.AllowedTools != null)
            sb.Append($" --allowedTools \"{string.Join(" ", cfg.AllowedTools)}\"");

        if (cfg.DisallowedTools != null)
            sb.Append($" --disallowedTools \"{string.Join(" ", cfg.DisallowedTools)}\"");

        if (cfg.McpServers != null && cfg.McpServers.Count > 0)
        {
            var mcpFile = Path.Combine(sessionLogDir, $"{safePathName}-mcp.json");
            var wrapper = new Dictionary<string, object> { ["mcpServers"] = cfg.McpServers };
            File.WriteAllText(mcpFile, JsonSerializer.Serialize(wrapper));
            sb.Append($" --mcp-config \"{SessionManager.EscapeForCmdQuoted(mcpFile)}\"");
            temps.Add(mcpFile);
            if (cfg.StrictMcp == true) sb.Append(" --strict-mcp-config");
        }

        if (cfg.Agents != null && cfg.Agents.Count > 0)
        {
            var inline = JsonSerializer.Serialize(cfg.Agents);
            sb.Append($" --agents \"{SessionManager.EscapeForCmdQuoted(inline)}\"");
        }

        if (!string.IsNullOrEmpty(cfg.PermissionMode))
            sb.Append($" --permission-mode {cfg.PermissionMode}");

        if (cfg.AddDirs != null)
            foreach (var d in cfg.AddDirs)
                sb.Append($" --add-dir \"{SessionManager.EscapeForCmdQuoted(d)}\"");

        if (cfg.SettingsOverride != null && cfg.SettingsOverride.Count > 0)
        {
            var settingsFile = Path.Combine(sessionLogDir, $"{safePathName}-settings.json");
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(cfg.SettingsOverride));
            sb.Append($" --settings \"{SessionManager.EscapeForCmdQuoted(settingsFile)}\"");
            temps.Add(settingsFile);
        }

        return new FlagBuildResult(sb.ToString(), temps);
    }
}
