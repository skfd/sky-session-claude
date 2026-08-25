namespace SessionCore;

/// <summary>A folder standby will open a session in.</summary>
public sealed record StandbyTarget
{
    public required string Folder { get; init; }

    /// <summary>The folder's leaf name — what the project is called everywhere else.</summary>
    public required string Project { get; init; }

    /// <summary>The most recent real turn in any session that ran there.</summary>
    public required DateTime LastActive { get; init; }
}

/// <summary>A folder standby considered and passed over, with the reason it did.</summary>
public sealed record StandbySkip
{
    public required string Folder { get; init; }
    public required string Project { get; init; }
    public required string Reason { get; init; }
}

public sealed record StandbyPlan
{
    public required IReadOnlyList<StandbyTarget> Open { get; init; }
    public required IReadOnlyList<StandbySkip> Skipped { get; init; }
}

/// <summary>
/// Which projects to leave a phone-reachable session in.
///
/// The desk and the phone want opposite things from a session list. At the desk a session is
/// found by remembering what it was about, so every one you ever worked in is worth keeping.
/// From a phone there is no terminal to open and no folder to <c>cd</c> into: a project you
/// can reach is one that already has a session running with Remote Control connected, and a
/// project that does not is simply not there. So this reads recency off the transcripts and
/// answers with the folders worth having up before you leave the desk.
///
/// It is folders, not sessions, deliberately — the session it opens is a fresh one. Resuming
/// the newest would carry a full context window and whatever the last conversation ended
/// mid-thought about into a phone screen; the ask from a phone is almost always a new one.
///
/// Pure, like <see cref="RestartPolicy"/> and <see cref="ClosePolicy"/>: the caller supplies
/// the scan, the registry and the clock, and gets back a plan it can print without having
/// opened anything.
/// </summary>
public static class Standby
{
    /// <summary>How far back "recently worked on" reaches when the caller does not say.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <param name="sessions">Scanned sessions — only their folder and last turn are read.</param>
    /// <param name="live">The registry, for the projects that are already reachable.</param>
    /// <param name="now">The clock, injected so a test can pick one.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="max">Cap on how many folders come back; the newest survive.</param>
    /// <param name="folderExists">
    /// Whether a folder is still there. A repo that has been deleted or a worktree that was
    /// merged away still has its transcripts, and a terminal opened in a folder that is gone
    /// starts in whatever the shell falls back to — a session on standby in the wrong place
    /// is worse than one that never opened.
    /// </param>
    /// <param name="isRepo">
    /// Whether a folder is a project rather than somewhere a question got asked. See
    /// <see cref="HasGit"/> for why the test is a <c>.git</c>.
    /// </param>
    public static StandbyPlan Decide(
        IEnumerable<SessionInfo> sessions,
        IEnumerable<LiveSession> live,
        DateTime now,
        TimeSpan? window = null,
        int max = int.MaxValue,
        Func<string, bool>? folderExists = null,
        Func<string, bool>? isRepo = null)
    {
        var since = now - (window ?? DefaultWindow);
        var exists = folderExists ?? Directory.Exists;
        var repo = isRepo ?? HasGit;

        var running = live as IReadOnlyCollection<LiveSession> ?? live.ToList();

        var newest = new Dictionary<string, (string Folder, DateTime LastActive)>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            if (session.RealCwd is not { Length: > 0 } cwd) continue;
            if (session.LastActive < since) continue;
            if (IsScratch(cwd)) continue;

            var key = Key(cwd);
            if (newest.TryGetValue(key, out var seen) && seen.LastActive >= session.LastActive) continue;
            newest[key] = (cwd, session.LastActive);
        }

        var open = new List<StandbyTarget>();
        var skipped = new List<StandbySkip>();

        foreach (var folder in newest.Values.OrderByDescending(e => e.LastActive))
        {
            var project = ProjectOf(folder.Folder);

            if (ReachableIn(running, folder.Folder) is { } already)
            {
                skipped.Add(new StandbySkip
                {
                    Folder = folder.Folder,
                    Project = project,
                    Reason = AlreadyReason(already),
                });
                continue;
            }

            if (!exists(folder.Folder))
            {
                skipped.Add(new StandbySkip
                {
                    Folder = folder.Folder,
                    Project = project,
                    Reason = "the folder is no longer there",
                });
                continue;
            }

            // After the existence check, never before it: a repo that has been deleted is
            // gone, not un-versioned, and reporting the wrong reason sends you looking in
            // the wrong place.
            if (!repo(folder.Folder))
            {
                skipped.Add(new StandbySkip
                {
                    Folder = folder.Folder,
                    Project = project,
                    Reason = "no .git — a folder you asked something in, not a project",
                });
                continue;
            }

            open.Add(new StandbyTarget
            {
                Folder = folder.Folder,
                Project = project,
                LastActive = folder.LastActive,
            });
        }

        // The cap is applied after the skips are decided, so a project reported as already
        // reachable never costs one of the slots — the count in the plan is what will open.
        var capped = max < open.Count ? open.Take(max).ToList() : open;
        if (capped.Count < open.Count)
            foreach (var dropped in open.Skip(max))
                skipped.Add(new StandbySkip
                {
                    Folder = dropped.Folder,
                    Project = dropped.Project,
                    Reason = $"past the --recent {max} cut",
                });

        return new StandbyPlan { Open = capped, Skipped = skipped };
    }

    /// <summary>
    /// The session already answering the phone in <paramref name="folder"/>, or null.
    ///
    /// A folder is reachable when something is running there with Remote Control connected —
    /// a busy session at the desk does not count, because it is the bridge, not the terminal,
    /// that a phone can see.
    /// </summary>
    public static LiveSession? ReachableIn(IEnumerable<LiveSession> live, string folder)
    {
        var key = Key(folder);
        return live.FirstOrDefault(l =>
            l.RemoteControl
            && !string.IsNullOrEmpty(l.Cwd)
            && Key(l.Cwd!).Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a folder is a project, which here means whether it is a git repo.
    ///
    /// Sessions get started in places that are not projects: a home folder, the folder every
    /// repo sits under, wherever a terminal happened to be open when a question came up. They
    /// are indistinguishable from repos in a transcript — the same recency, the same session
    /// count — and they cost one row each on a phone screen that has nothing but rows.
    ///
    /// The <c>.git</c> may be a folder or a file; a worktree and a submodule write a file.
    /// This is only the sweep's definition: <c>standby --in &lt;path&gt;</c> is you pointing,
    /// and pointing at a folder is a better answer than any rule about it.
    /// </summary>
    public static bool HasGit(string folder)
    {
        var git = Path.Combine(folder, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>How a folder that is already reachable is reported.</summary>
    public static string AlreadyReason(LiveSession already) =>
        $"already on standby — \"{already.Name ?? already.SessionId}\" has Remote Control connected";

    /// <summary>What the project in a folder is called: the folder's leaf name.</summary>
    public static string ProjectOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// Folders an agent made rather than folders you work in. Worktrees under a repo's
    /// <c>.claude</c> live for one task and are deleted after it, so they read as freshly
    /// worked-in projects at exactly the moment they stop existing — and a phone list of
    /// four repos and eleven of their worktrees is not a list anyone can use.
    /// </summary>
    private static bool IsScratch(string folder) =>
        folder.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(".claude", StringComparison.OrdinalIgnoreCase));

    /// <summary>How two spellings of the same folder are told to be the same folder.</summary>
    private static string Key(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

}
