namespace Huddle;

public enum QueueState { Queued, Active, Done, Failed }

/// <summary>
/// A declared unit of dispatched work. Files = scope it will touch; DependsOn = prerequisite
/// unit ids that must reach Done first. Objective/Owner are carried for future cross-machine
/// awareness and unused by the v1 intra-machine queue.
/// </summary>
public record WorkUnit(
    string Id,
    string Repo,
    string Persona,
    string Prompt,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> DependsOn,
    string? Objective = null,
    string? Owner = null,
    string? Project = null);   // project slug (projects phase 1); null when unstamped
