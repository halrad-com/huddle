namespace CmdPalHuddle;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CmdPalHuddle.Models;

public sealed class HuddleClient
{
    private readonly HuddleSettings _settings;

    public HuddleClient(HuddleSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Returns the directory containing huddle.json, or null if it cannot be found.
    /// Order: explicit setting → walk-up from a running huddle.exe → null.
    /// </summary>
    public string? GetHuddleRoot()
    {
        var explicitRoot = _settings.HuddleRoot;
        if (!string.IsNullOrWhiteSpace(explicitRoot)
            && File.Exists(Path.Combine(explicitRoot, "huddle.json")))
        {
            return explicitRoot;
        }

        var runningExe = FindRunningHuddleExePath();
        if (runningExe is not null)
        {
            var root = WalkUpForHuddleJson(Path.GetDirectoryName(runningExe));
            if (root is not null) return root;
        }

        return null;
    }

    private static string? FindRunningHuddleExePath()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("huddle"))
            {
                using (p)
                {
                    try { return p.MainModule?.FileName; }
                    catch { /* access denied — try next */ }
                }
            }
        }
        catch { /* enumeration failed — give up */ }
        return null;
    }

    private static string? WalkUpForHuddleJson(string? startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "huddle.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// True if any huddle.exe process is currently running on this machine.
    /// Cheap; fine to call per page render.
    /// </summary>
    public bool IsHuddleAlive()
    {
        try
        {
            var processes = Process.GetProcessesByName("huddle");
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<RepoInfo> GetRepos()
    {
        var root = GetHuddleRoot();
        if (root is null) return Array.Empty<RepoInfo>();

        var configPath = Path.Combine(root, "huddle.json");
        JsonNode? doc;
        try { doc = JsonNode.Parse(File.ReadAllText(configPath)); }
        catch { return Array.Empty<RepoInfo>(); }

        var sessionsArr = doc?["sessions"] as JsonArray;
        if (sessionsArr is null) return Array.Empty<RepoInfo>();

        var result = new List<RepoInfo>(sessionsArr.Count);
        foreach (var node in sessionsArr)
        {
            if (node is null) continue;
            var name = node["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var aliases = (node["aliases"] as JsonArray)?
                .Select(a => a?.GetValue<string>() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray()
                ?? Array.Empty<string>();

            var rootPath = node["root"]?.GetValue<string>() ?? string.Empty;
            var purpose = node["purpose"]?.GetValue<string>();

            result.Add(new RepoInfo(name, aliases, rootPath, purpose));
        }
        return result;
    }

    public IReadOnlyList<PersonaInfo> GetPersonas()
    {
        var root = GetHuddleRoot();
        if (root is null) return Array.Empty<PersonaInfo>();

        var dir = Path.Combine(root, "personas");
        if (!Directory.Exists(dir)) return Array.Empty<PersonaInfo>();

        var result = new List<PersonaInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith('_')) continue;       // _shared.md and friends

            string? subtitle = null;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.TrimStart('#', ' ').Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    subtitle = trimmed.Length > 100 ? trimmed[..100] + "…" : trimmed;
                    break;
                }
            }
            catch { /* swallow — subtitle stays null */ }

            result.Add(new PersonaInfo(name, subtitle, path));
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<SessionInfo> GetSessions()
    {
        var root = GetHuddleRoot();
        if (root is null) return Array.Empty<SessionInfo>();

        var ipcDir = Path.Combine(root, "ipc");
        if (!Directory.Exists(ipcDir)) return Array.Empty<SessionInfo>();

        var result = new List<SessionInfo>();
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);

        foreach (var sessionDir in Directory.EnumerateDirectories(ipcDir))
        {
            var safeName = Path.GetFileName(sessionDir);
            if (safeName is "_huddle" or "workledger") continue;

            // Convention: safe-name is "<repo>_<persona>" (underscore delimiter).
            var parts = safeName.Split('_', 2);
            if (parts.Length != 2) continue;
            var (repo, persona) = (parts[0], parts[1]);
            var instanceId = $"{repo}:{persona}";

            var status = "idle";
            DateTimeOffset? startedAt = null;
            var inboxDir = Path.Combine(sessionDir, "inbox");
            var processedDir = Path.Combine(sessionDir, "processed");

            try
            {
                DateTime? mostRecent = null;
                foreach (var d in new[] { inboxDir, processedDir })
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.EnumerateFiles(d, "*.json"))
                    {
                        var ts = File.GetLastWriteTimeUtc(f);
                        if (mostRecent is null || ts > mostRecent) mostRecent = ts;
                    }
                }
                if (mostRecent is { } mr && mr > oneHourAgo) status = "running";
                startedAt = mostRecent is { } m ? new DateTimeOffset(m, TimeSpan.Zero) : null;
            }
            catch { /* keep defaults */ }

            result.Add(new SessionInfo(
                InstanceId: instanceId,
                SafeName: safeName,
                Repo: repo,
                Persona: persona,
                Root: null,
                StartedAt: startedAt,
                Status: status));
        }

        return result.OrderByDescending(s => s.StartedAt ?? DateTimeOffset.MinValue).ToList();
    }

    public IReadOnlyList<ConflictInfo> GetConflicts()
    {
        var root = GetHuddleRoot();
        if (root is null) return Array.Empty<ConflictInfo>();

        var workledgerDir = Path.Combine(root, "ipc", "workledger");
        if (!Directory.Exists(workledgerDir)) return Array.Empty<ConflictInfo>();

        var holdersByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sourceByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Ingest(string path, string source)
        {
            var holder = Path.GetFileNameWithoutExtension(path);
            IEnumerable<string> lines;
            try { lines = File.ReadAllLines(path); } catch { return; }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!line.StartsWith("- ")) continue;
                var file = line[2..].Trim().Trim('`', '"');
                if (string.IsNullOrEmpty(file)) continue;
                if (!file.Contains('/') && !file.Contains('\\')) continue;     // only path-shaped lines

                if (!holdersByFile.TryGetValue(file, out var list))
                {
                    list = new List<string>();
                    holdersByFile[file] = list;
                    sourceByFile[file] = source;
                }
                if (!list.Contains(holder)) list.Add(holder);
            }
        }

        foreach (var f in Directory.EnumerateFiles(workledgerDir, "*.md")) Ingest(f, "freeform");
        var claimsDir = Path.Combine(workledgerDir, "claims");
        if (Directory.Exists(claimsDir))
            foreach (var f in Directory.EnumerateFiles(claimsDir, "*.md")) Ingest(f, "claim");

        return holdersByFile
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new ConflictInfo(kv.Key, kv.Value, sourceByFile[kv.Key]))
            .OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Drops an IPC envelope into the target session's inbox.
    /// Returns the final filename (without directory) on success, or null on failure.
    /// targetSafeName: "_huddle" for orchestrator commands, "<repo>_<persona>" for session-targeted.
    /// </summary>
    public string? WriteCommand(string targetSafeName, string subject, string type, JsonNode? body)
    {
        var root = GetHuddleRoot();
        if (root is null) return null;

        var inbox = Path.Combine(root, "ipc", targetSafeName, "inbox");
        try { Directory.CreateDirectory(inbox); } catch { return null; }

        var envelope = new IpcEnvelope
        {
            From = "cmdpal:huddle",
            To = targetSafeName,
            Timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ"),
            Type = type,
            Subject = subject,
            Body = body
        };

        var ordinal = NextOrdinal(inbox);
        var fileName = $"{ordinal:D3}-from-cmdpal-huddle-{envelope.Timestamp}.json";
        var finalPath = Path.Combine(inbox, fileName);
        var tmpPath = finalPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, finalPath);
            return fileName;
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            return null;
        }
    }

    private static int NextOrdinal(string inboxDir)
    {
        var max = 0;
        foreach (var f in Directory.EnumerateFiles(inboxDir, "*.json"))
        {
            var name = Path.GetFileName(f);
            var dash = name.IndexOf('-');
            if (dash < 1) continue;
            if (int.TryParse(name[..dash], out var n) && n > max) max = n;
        }
        return max + 1;
    }
}
