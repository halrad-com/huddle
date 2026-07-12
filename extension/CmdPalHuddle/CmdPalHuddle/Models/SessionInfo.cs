namespace CmdPalHuddle.Models;

using System;

public sealed record SessionInfo(
    string InstanceId,        // "repo:persona"
    string SafeName,          // "repo_persona" — used in ipc/ paths
    string Repo,
    string Persona,
    string? Root,
    DateTimeOffset? StartedAt,
    string Status);           // "running" | "crashed" | "unknown"
