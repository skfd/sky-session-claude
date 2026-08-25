using SessionCore;

namespace SessionCli;

/// <summary>
/// The JSON contract. Field names and order mirror the original
/// <c>get-claudesessions.ps1 -Json</c> so the morning brief needs no changes; everything
/// the agent-facing verbs added is appended after, which no reader of named fields notices.
/// </summary>
internal sealed class ExportDto
{
    public required string GeneratedAt { get; init; }
    public required int Count { get; init; }
    public required List<SessionDto> Sessions { get; init; }

    /// <summary>Only present when something went wrong that the caller should hear about.</summary>
    public string? Warning { get; init; }
}

/// <summary>What a live session adds to a card: the process, and whether we may restart it.</summary>
internal sealed class LiveDto
{
    public required int Pid { get; init; }
    public required string? Version { get; init; }

    /// <summary>"busy" mid-turn, "idle" at the prompt, "waiting" on an answer from you.</summary>
    public required string? State { get; init; }

    public required bool RemoteControl { get; init; }

    /// <summary>Running an older build than the one installed — the reason to restart it.</summary>
    public required bool Stale { get; init; }

    /// <summary>"safe", "ask" or "unsafe" — see RestartPolicy.</summary>
    public required string Restart { get; init; }

    public required string RestartReason { get; init; }

    public static LiveDto From(LiveSession live, SessionStatus? tail, string? installed)
    {
        var verdict = RestartPolicy.Judge(live, tail, DateTime.Now);
        return new LiveDto
        {
            Pid = live.Pid,
            Version = live.Version,
            State = live.Status,
            RemoteControl = live.RemoteControl,
            Stale = ClaudeInstall.IsStale(live.Version, installed),
            Restart = verdict.Safety switch
            {
                SweepSafety.Safe => "safe",
                SweepSafety.Ask => "ask",
                _ => "unsafe",
            },
            RestartReason = verdict.Reason,
        };
    }
}

internal sealed class SessionDto
{
    public required DateTime LastActive { get; init; }
    public required DateTime LastTouched { get; init; }
    public required DateTime? PreviousActive { get; init; }
    public required double AgeDays { get; init; }
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string Status { get; init; }
    public required bool Complete { get; init; }
    public required int? ContextPct { get; init; }
    public required int ContextTokens { get; init; }
    public required string LastPrompt { get; init; }
    public required string Recap { get; init; }
    public required bool Unfinished { get; init; }
    public required string WaitingOn { get; init; }
    public required string Cwd { get; init; }
    public required string SessionId { get; init; }
    public required double SizeKB { get; init; }
    public required string Command { get; init; }

    // --- added for the agent-facing verbs -----------------------------------

    /// <summary>"none", "done" or "abandoned" — the operator's mark, never the classifier's.</summary>
    public required string Disposition { get; init; }

    /// <summary>
    /// Nothing left to do here: the classifier said complete, or someone marked it done.
    /// This is what "hide completed" hides, and the one field an agent should filter on to
    /// find work that is still outstanding.
    /// </summary>
    public required bool Settled { get; init; }

    /// <summary>Null unless the session is open in a terminal right now.</summary>
    public LiveDto? Live { get; init; }

    public static SessionDto From(SessionInfo s, Disposition disposition, LiveDto? live) => new()
    {
        LastActive = s.LastActive,
        LastTouched = s.LastTouched,
        PreviousActive = s.PreviousActive,
        AgeDays = s.AgeDays,
        Name = s.Name ?? "(untitled)",
        Project = s.Project,
        Status = s.Status.ToWire(),
        Complete = s.Complete,
        ContextPct = s.ContextPct,
        ContextTokens = s.ContextTokens,
        LastPrompt = s.LastPrompt,
        Recap = s.Recap,
        Unfinished = s.Unfinished,
        WaitingOn = s.WaitingOn,
        Cwd = s.Cwd ?? "",
        SessionId = s.SessionId,
        SizeKB = s.SizeKB,
        Command = s.Command,
        Disposition = DispositionStore.ToWire(disposition),
        Settled = s.Complete || disposition == SessionCore.Disposition.Done,
        Live = live,
    };
}

/// <summary>One place a fork can branch from, as reported by <c>show</c>.</summary>
internal sealed class ForkPointDto
{
    public required int Prompt { get; init; }
    public required string Text { get; init; }
    public required DateTime? At { get; init; }

    public static ForkPointDto From(ForkPoint p) => new()
    {
        Prompt = p.Ordinal,
        Text = p.Prompt,
        At = p.Timestamp,
    };
}

/// <summary>A session in full, for <c>show</c>: everything on the card plus where it can fork.</summary>
internal sealed class ShowDto
{
    public required SessionDto Session { get; init; }
    public required string FilePath { get; init; }
    public required List<ForkPointDto> ForkPoints { get; init; }
}

/// <summary>
/// The envelope every mutating verb returns. One shape for all of them, so a caller can
/// check <c>Ok</c> without knowing which verb it ran.
/// </summary>
internal sealed class ActionResult
{
    public required bool Ok { get; init; }
    public required string Action { get; init; }
    public required string Message { get; init; }
    public List<ActionItem>? Items { get; init; }

    /// <summary>What `peek` read off a terminal; null for every other verb.</summary>
    public string? Screen { get; init; }
}

internal sealed class ActionItem
{
    public required string SessionId { get; init; }
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public string? Name { get; init; }

    /// <summary>The id a fork produced; null for every other verb.</summary>
    public string? NewSessionId { get; init; }

    /// <summary>
    /// The <c>skysession://</c> link <c>link</c> produced; null for every other verb. Its own
    /// field rather than only in the message, because the caller that wants this is a brief
    /// writing an href and should not have to find it in a sentence.
    /// </summary>
    public string? Link { get; init; }
}
