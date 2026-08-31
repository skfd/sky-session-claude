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

    /// <summary>
    /// The Remote Control hosts, when <c>--stale</c> asked about them. Their own array
    /// rather than rows among the sessions, because a host has none of a session's fields —
    /// no status, no context, no id — and filling those in with blanks is exactly the
    /// conflation the glossary's Runtime section exists to stop. <c>Count</c> and
    /// <c>Sessions</c> therefore keep meaning what they always did.
    /// </summary>
    public List<HostDto>? Hosts { get; init; }

    /// <summary>
    /// One row per project folder, when <c>--projects</c> asked the question a level up.
    /// It answers <i>instead of</i> the sessions rather than alongside them — the roll-up is
    /// the whole point of asking, and a phone brief that wants it does not want two hundred
    /// session rows underneath. <see cref="ProjectDto.SessionIds"/> is the way back down.
    /// </summary>
    public List<ProjectDto>? Projects { get; init; }

    /// <summary>Only present when something went wrong that the caller should hear about.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// A project as a row: what the whole folder is waiting on, who said so, and what is running
/// there. The derived part comes from <see cref="ProjectFold"/> and is pure; the runtime facts
/// are attached here, where there is a registry to ask.
/// </summary>
internal sealed class ProjectDto
{
    public required string Project { get; init; }
    public required string Folder { get; init; }

    /// <summary>
    /// "broken", "blocked", "needs-read", "runnable", "undeclared", "exhausted", "quiet" or
    /// "abandoned" — see ProjectFold. Ordered most urgent first in this array.
    /// </summary>
    public required string State { get; init; }

    /// <summary>The note behind the state, when a declaration decided it rather than the fold.</summary>
    public required string? Note { get; init; }

    /// <summary>The session the state came from, so a caller can go and look at it.</summary>
    public required string? StateFrom { get; init; }

    /// <summary>Sessions that counted. Crossed-out ones are not among them.</summary>
    public required int Sessions { get; init; }

    /// <summary>How many of those are not settled.</summary>
    public required int Unfinished { get; init; }

    /// <summary>How many were crossed out and skipped.</summary>
    public required int Abandoned { get; init; }

    /// <summary>
    /// How many carry a declaration that still stands. Zero on a project full of unfinished
    /// work is the measurement this feature exists to take: nobody there is reporting.
    /// </summary>
    public required int Declared { get; init; }

    /// <summary>How many of its sessions are open in a terminal right now.</summary>
    public required int Live { get; init; }

    /// <summary>How many of those are behind the installed build.</summary>
    public required int Stale { get; init; }

    /// <summary>Whether a <c>claude rc</c> host is serving this folder — is it reachable by phone.</summary>
    public required bool Host { get; init; }

    /// <summary>
    /// Every session here, newest first — the key back to the rows this listing replaced.
    /// Crossed-out sessions are included: they are part of what is here, they just did not
    /// decide anything.
    /// </summary>
    public required IReadOnlyList<string> SessionIds { get; init; }

    public static ProjectDto From(ProjectRoll roll, int live, int stale, bool host) => new()
    {
        Project = roll.Project,
        Folder = roll.Folder,
        State = ProjectFold.ToWire(roll.State),
        Note = roll.Note,
        StateFrom = roll.StateFrom,
        Sessions = roll.Sessions,
        Unfinished = roll.Unfinished,
        Abandoned = roll.Abandoned,
        Declared = roll.Declared,
        Live = live,
        Stale = stale,
        Host = host,
        SessionIds = roll.SessionIds,
    };
}

/// <summary>
/// A <c>claude rc</c> host as a row: what is running, how far behind it is, and whether the
/// sweep would take it.
/// </summary>
internal sealed class HostDto
{
    public required string Project { get; init; }
    public required string Folder { get; init; }
    public required int Pid { get; init; }

    /// <summary>The bridge session the phone addresses. Not a Claude session id — no verb takes it.</summary>
    public required string BridgeSessionId { get; init; }

    /// <summary>Running since. A host publishes no version, so this is the age you get.</summary>
    public required DateTime? Started { get; init; }

    /// <summary>An update has renamed its binary out from under it — see ClaudeInstall.IsSuperseded.</summary>
    public required bool Stale { get; init; }

    /// <summary>How many conversations it is answering for, which is what decides the verdict.</summary>
    public required int Serving { get; init; }

    /// <summary>"safe", "ask" or "unsafe" — see HostRestartPolicy.</summary>
    public required string Restart { get; init; }

    public required string RestartReason { get; init; }

    /// <summary>The line that puts it back, ready to paste — the same one a restart types.</summary>
    public required string Command { get; init; }

    public static HostDto From(RemoteControlHost host, SweepVerdict verdict, int serving) => new()
    {
        Project = host.Project,
        Folder = host.Folder,
        Pid = host.Pid,
        BridgeSessionId = host.BridgeSessionId,
        Started = host.Started,
        Stale = host.Stale,
        Serving = serving,
        Restart = verdict.Safety switch
        {
            SweepSafety.Safe => "safe",
            SweepSafety.Ask => "ask",
            _ => "unsafe",
        },
        RestartReason = verdict.Reason,
        Command = LaunchLine.HostAgain(host.Folder, host.CommandLine),
    };
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
    /// This is what "hide completed" hides. It is not by itself the answer to "what is
    /// still outstanding" — an abandoned session is not settled and is still off the hook,
    /// which is why <c>--unfinished</c> drops both.
    /// </summary>
    public required bool Settled { get; init; }

    /// <summary>
    /// The session's own claim about what happens next — "runnable", "blocked", "needs-read",
    /// "exhausted" — while it still stands, else "none". The third axis, next to
    /// <see cref="Status"/> and <see cref="Disposition"/>; it never changes either.
    /// </summary>
    public required string Declared { get; init; }

    /// <summary>
    /// A declaration exists but the operator has prompted since, so it no longer stands.
    /// Distinct from never-declared on purpose: "this agent reported and things moved on"
    /// and "this agent never reports" are different failures of the convention.
    /// </summary>
    public required bool DeclaredStale { get; init; }

    /// <summary>Null unless the session is open in a terminal right now.</summary>
    public LiveDto? Live { get; init; }

    public static SessionDto From(
        SessionInfo s, Disposition disposition, Declaration? claim, LiveDto? live) => new()
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
        Declared = claim is not null && claim.StillStands(s)
            ? ProjectFold.ToWire(claim.State)
            : "none",
        DeclaredStale = claim is not null && !claim.StillStands(s),
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

    /// <summary>
    /// Something the caller should hear about that is not the verb failing — a sidecar that
    /// could not be read, say. Ok stays true: the action was taken, and this is the news that
    /// it may not have been saved.
    /// </summary>
    public string? Warning { get; init; }
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
    /// The folder a row is about, for `standby` — the one verb whose rows are projects
    /// rather than sessions, because the session it reports does not exist yet. Null
    /// everywhere else, where <see cref="SessionId"/> already says which one is meant.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>
    /// The <c>skysession://</c> link <c>link</c> produced; null for every other verb. Its own
    /// field rather than only in the message, because the caller that wants this is a brief
    /// writing an href and should not have to find it in a sentence.
    /// </summary>
    public string? Link { get; init; }
}
