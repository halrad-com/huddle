namespace CmdPalHuddle.Models;

using System.Collections.Generic;

public sealed record ConflictInfo(
    string FilePath,
    IReadOnlyList<string> Holders,    // session safe-names
    string Source);                   // "freeform" | "claim"
