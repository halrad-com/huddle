namespace CmdPalHuddle.Models;

using System.Collections.Generic;

public sealed record RepoInfo(
    string Name,
    IReadOnlyList<string> Aliases,
    string Root,
    string? Purpose);
