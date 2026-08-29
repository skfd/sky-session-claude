namespace SessionCore;

/// <summary>
/// What an agent said about a session's future — the third axis, next to the classifier's
/// derived <see cref="SessionStatus"/> and the operator's declared <see cref="Disposition"/>.
///
/// These are the claims the file cannot answer for itself (see docs/PROJECT-STATE.md):
/// whether work remains, whether anything follows once the ball comes back, and whether the
/// agent's own output is worth reading. Nobody outside the session knows, so the session says.
/// </summary>
public enum Declared
{
    /// <summary>Nothing declared, or what was declared has expired.</summary>
    None,

    /// <summary>Work is queued and needs no decision — wake it and walk away.</summary>
    Runnable,

    /// <summary>Held by the operator, and work follows once they answer.</summary>
    Blocked,

    /// <summary>Over, but there is a report here worth the operator's eyes.</summary>
    NeedsRead,

    /// <summary>Over, and there is nothing here — whatever the last turn looks like.</summary>
    Exhausted,
}

/// <summary>
/// One agent's claim about one session, and the turn it was made at.
///
/// <see cref="AtTurn"/> is what keeps it honest: agents forget to re-declare, and a confident
/// wrong "exhausted" is worse than no claim at all. It holds the uuid of the last operator
/// prompt at the moment of the declaration, and the claim is dead the moment the operator
/// says something new — see <see cref="SessionFileFields.LastPromptUuid"/> for why that is
/// the anchor rather than the last turn.
/// </summary>
public sealed record Declaration
{
    public required Declared State { get; init; }

    /// <summary>
    /// The one line the operator reads on the card — what is queued, or who the blocker is.
    /// A blocked state without a named blocker rots; this is what stops it.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>The last operator prompt when this was written; null when the file had none.</summary>
    public string? AtTurn { get; init; }

    /// <summary>When it was written, for a card that wants to say how old the claim is.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// Whether this claim still stands over <paramref name="session"/>. Two nulls count as a
    /// match — a session with no operator prompt at all, a <c>-p</c> run say, has nothing that
    /// could have moved. A null anchor against a file that does have a prompt does not: a
    /// claim that never recorded where it was made cannot be checked, and unverifiable is the
    /// same as stale.
    /// </summary>
    public bool StillStands(SessionInfo session) =>
        string.Equals(AtTurn, session.LastPromptUuid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a whole project is waiting on. Declared in fold order, most urgent first, so the
/// roll-up is a minimum — a project is as urgent as its most urgent session.
/// </summary>
public enum ProjectState
{
    /// <summary>Died mid-work. Revive it; there is no decision to make.</summary>
    Broken,

    /// <summary>An agent said it is held by the operator and work follows the answer.</summary>
    Blocked,

    /// <summary>An agent said it is over and left something worth reading.</summary>
    NeedsRead,

    /// <summary>Work is queued, or already in flight — either way, walk away.</summary>
    Runnable,

    /// <summary>Something is unfinished and nobody said what it needs.</summary>
    Undeclared,

    /// <summary>An agent looked at unfinished-looking work and said it is over.</summary>
    Exhausted,

    /// <summary>Nothing was pending to begin with.</summary>
    Quiet,

    /// <summary>Every session here is crossed out. The operator said no.</summary>
    Abandoned,
}

/// <summary>One project, rolled up.</summary>
public sealed record ProjectRoll
{
    /// <summary>The folder's leaf name — what the project is called everywhere else.</summary>
    public required string Project { get; init; }

    /// <summary>The folder itself, which is what the fold actually keys on.</summary>
    public required string Folder { get; init; }

    public required ProjectState State { get; init; }

    /// <summary>
    /// The note from the declaration that decided <see cref="State"/>, when one did. Null
    /// when the state was derived, which is most of the time until agents start declaring.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>The session the state came from, so a card can point at it.</summary>
    public string? StateFrom { get; init; }

    /// <summary>Sessions that counted. Abandoned ones are not among them.</summary>
    public required int Sessions { get; init; }

    /// <summary>How many of those are not settled.</summary>
    public required int Unfinished { get; init; }

    /// <summary>How many were crossed out and skipped.</summary>
    public required int Abandoned { get; init; }

    /// <summary>How many carried a declaration that still stands.</summary>
    public required int Declared { get; init; }

    /// <summary>
    /// Every session folded in, newest first — the key back to the session rows, since
    /// <c>list --projects</c> answers with projects instead of them. Abandoned sessions are
    /// included: they are part of what is here, they just do not decide anything.
    /// </summary>
    public required IReadOnlyList<string> SessionIds { get; init; }
}

/// <summary>
/// Rolls a scan up into one row per project — what a whole project is waiting on, rather than
/// what each conversation in it is doing.
///
/// Pure, like <see cref="RestartPolicy"/>, <see cref="ClosePolicy"/> and <see cref="Standby"/>:
/// the caller supplies the scan, the operator's marks and whatever agents have declared, and
/// gets back a roll-up it can print without having opened anything.
///
/// Three sources answer in a fixed order, and the order is the whole design:
/// <list type="number">
/// <item>The <b>operator's</b> disposition. Abandoned is skipped outright — it is unfinished
///       and not coming back, so it must never be what makes a project read
///       <see cref="ProjectState.Undeclared"/> — and Done folds as settled, because settled,
///       not <c>complete</c>, is what "nothing left to do" means here.</item>
/// <item>The <b>agent's</b> declaration, while it still stands. This is consulted before the
///       classifier, and that is the point of the whole feature: a session that ends on
///       "want me to push?" reads <c>waiting-you</c> forever and no sharpening of the
///       classifier will ever say otherwise. Law 1 holds — the session's own Status is
///       untouched and its card still says what the file says. Only the project moves.</item>
/// <item>The <b>classifier's</b> Status, for everything nobody spoke about.</item>
/// </list>
///
/// What the classifier is allowed to conclude on its own is deliberately narrow. A session
/// waiting on the agent is <see cref="ProjectState.Runnable"/>, because waking it is the right
/// move whatever follows. A session waiting on the operator is <b>not</b>
/// <see cref="ProjectState.Blocked"/>: "answer, and three more things happen" and "that was
/// the last question" are the same file, and guessing between them is the mistake this whole
/// design exists to avoid. It reads <see cref="ProjectState.Undeclared"/> — something is
/// unfinished and nobody said what it needs — which is honest, and which is also the
/// measurement: until agents declare, that is what most projects will say.
/// </summary>
public static class ProjectFold
{
    /// <param name="sessions">The scan. Only sessions with a known folder can join a project.</param>
    /// <param name="dispositionOf">The operator's mark for a session id.</param>
    /// <param name="declarationOf">
    /// What an agent declared about a session id, if anything. Expiry is checked here rather
    /// than by the caller, so a store that hands back everything it holds is still safe.
    /// </param>
    /// <param name="isWorking">
    /// Whether a session is open in a terminal and mid-turn right now. The one runtime fact
    /// the fold cannot do without, because without it the most active project on the machine
    /// reads <see cref="ProjectState.Broken"/>: a session taking a turn ends its file on a
    /// <c>tool_use</c>, which is exactly what a session that died mid-tool looks like, and the
    /// classifier is right to call both <c>cut-off</c>. What tells them apart is not in the
    /// file at all — it is whether the process is still there.
    /// </param>
    public static IReadOnlyList<ProjectRoll> Roll(
        IEnumerable<SessionInfo> sessions,
        Func<string, Disposition>? dispositionOf = null,
        Func<string, Declaration?>? declarationOf = null,
        Func<string, bool>? isWorking = null)
    {
        var mark = dispositionOf ?? (_ => Disposition.None);
        var claim = declarationOf ?? (_ => null);
        var working = isWorking ?? (_ => false);

        var groups = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            // A session with no recorded cwd joins no project. Cwd is never empty — it holds
            // SessionInfo.UnknownCwd — so folding on it would invent a project named after
            // the sentence, which is how a session once ended up called
            // "unknown-cwd-not-found-in-session-file-b9".
            if (session.RealCwd is not { Length: > 0 } cwd) continue;

            var key = Key(cwd);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<SessionInfo>();
                folders[key] = cwd;
            }
            list.Add(session);
        }

        return groups
            .Select(g => One(folders[g.Key], g.Value, mark, claim, working))
            .OrderBy(r => r.State)
            .ThenBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProjectRoll One(
        string folder,
        List<SessionInfo> sessions,
        Func<string, Disposition> mark,
        Func<string, Declaration?> claim,
        Func<string, bool> working)
    {
        var newestFirst = sessions.OrderByDescending(s => s.LastActive).ToList();

        // What a project of nothing but crosses reads as. Every other state is more urgent,
        // so the first session that counts will replace it.
        var state = ProjectState.Abandoned;
        string? note = null;
        string? from = null;
        int counted = 0, unfinished = 0, abandoned = 0, declared = 0;

        foreach (var session in newestFirst)
        {
            var disposition = mark(session.SessionId);
            if (disposition == Disposition.Abandoned)
            {
                abandoned++;
                continue;
            }

            counted++;

            var live = Live(session, claim);
            if (live is not null) declared++;

            var one = Of(session, disposition, live, working(session.SessionId));
            if (one != ProjectState.Quiet) unfinished++;

            if (one < state)
            {
                state = one;
                note = live?.Note;
                from = session.SessionId;
            }
        }

        return new ProjectRoll
        {
            Project = Standby.ProjectOf(folder),
            Folder = folder,
            State = state,
            Note = note,
            StateFrom = from,
            Sessions = counted,
            Unfinished = unfinished,
            Abandoned = abandoned,
            Declared = declared,
            SessionIds = newestFirst.Select(s => s.SessionId).ToList(),
        };
    }

    /// <summary>The declaration standing over a session right now, or null.</summary>
    private static Declaration? Live(SessionInfo session, Func<string, Declaration?> claim) =>
        claim(session.SessionId) is { State: not SessionCore.Declared.None } d && d.StillStands(session)
            ? d
            : null;

    /// <summary>
    /// What one session contributes. Public because it is the rule, and a card that wants to
    /// say why its project reads the way it does should ask the same question the fold asked.
    /// </summary>
    public static ProjectState Of(
        SessionInfo session, Disposition mark, Declaration? live, bool working = false)
    {
        // The operator's word comes first, above the agent's and above the classifier's.
        // Abandoned never reaches here — Roll skips it before asking.
        if (mark == Disposition.Done) return ProjectState.Quiet;

        // Before the Complete check on purpose: "over, and there is a report here worth your
        // eyes" is a claim about a session that already looks finished, and it would be
        // unreachable if settled won first.
        if (live is not null)
            return live.State switch
            {
                SessionCore.Declared.Runnable => ProjectState.Runnable,
                SessionCore.Declared.Blocked => ProjectState.Blocked,
                SessionCore.Declared.NeedsRead => ProjectState.NeedsRead,
                SessionCore.Declared.Exhausted => ProjectState.Exhausted,
                _ => Derived(session.Status),
            };

        // A turn in flight is not a state anyone has to act on, and it is not a corpse. The
        // file cannot say which — a session mid-tool and a session that died mid-tool write
        // the same last record — so the process gets the last word over the classifier here,
        // and only here.
        if (working) return ProjectState.Runnable;

        return Derived(session.Status);
    }

    /// <summary>What the classifier alone is willing to say. Deliberately short.</summary>
    private static ProjectState Derived(SessionStatus status) => status switch
    {
        SessionStatus.Complete => ProjectState.Quiet,
        SessionStatus.Error or SessionStatus.Limit or SessionStatus.CutOff => ProjectState.Broken,
        SessionStatus.WaitingAgent => ProjectState.Runnable,

        // Interrupted lands here alongside waiting-you: you reached over and stopped it, so
        // the ball is yours and what happens next is yours to say.
        _ => ProjectState.Undeclared,
    };

    /// <summary>How two spellings of the same folder are told to be the same folder.</summary>
    private static string Key(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

    public static string ToWire(ProjectState state) => state switch
    {
        ProjectState.Broken => "broken",
        ProjectState.Blocked => "blocked",
        ProjectState.NeedsRead => "needs-read",
        ProjectState.Runnable => "runnable",
        ProjectState.Undeclared => "undeclared",
        ProjectState.Exhausted => "exhausted",
        ProjectState.Quiet => "quiet",
        _ => "abandoned",
    };

    public static string ToWire(Declared state) => state switch
    {
        SessionCore.Declared.Runnable => "runnable",
        SessionCore.Declared.Blocked => "blocked",
        SessionCore.Declared.NeedsRead => "needs-read",
        SessionCore.Declared.Exhausted => "exhausted",
        _ => "none",
    };

    /// <summary>
    /// The four a session may claim about itself, and nothing else. <c>broken</c>,
    /// <c>quiet</c>, <c>undeclared</c> and <c>abandoned</c> are things the fold works out —
    /// accepting them here would let an agent assert a fact rather than report an intention.
    /// </summary>
    public static Declared FromWire(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "runnable" => SessionCore.Declared.Runnable,
        "blocked" => SessionCore.Declared.Blocked,
        "needs-read" or "needsread" => SessionCore.Declared.NeedsRead,
        "exhausted" => SessionCore.Declared.Exhausted,
        _ => SessionCore.Declared.None,
    };

    /// <summary>What <c>state</c> accepts, for the message it prints when it does not.</summary>
    public static readonly string[] Declarable = ["runnable", "blocked", "needs-read", "exhausted"];
}
