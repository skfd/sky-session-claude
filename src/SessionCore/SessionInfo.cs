namespace SessionCore;

/// <summary>
/// Everything extracted from a single transcript. Mirrors the pscustomobject the
/// original Get-SessionInfo returned, plus the file-level fields Get-SessionRows added.
/// </summary>
public sealed class SessionInfo
{
    // --- from the transcript body -------------------------------------------
    public string? Cwd { get; init; }
    public string? Name { get; init; }          // custom title wins over AI title
    public string LastPrompt { get; init; } = "";
    public string Recap { get; init; } = "";
    public SessionStatus Status { get; init; }
    public bool Complete => Status == SessionStatus.Complete;
    public int ContextTokens { get; init; }
    public int? ContextPct { get; init; }

    // --- from the file on disk ----------------------------------------------
    public string SessionId { get; init; } = "";     // transcript file base name
    public DateTime LastActive { get; init; }
    public double AgeDays { get; init; }
    public double SizeKB { get; init; }

    // --- derived display fields ---------------------------------------------
    public string Project { get; init; } = "";
    public string Command { get; init; } = "";
    public bool Unfinished { get; init; }
    public string WaitingOn { get; init; } = "";
}
