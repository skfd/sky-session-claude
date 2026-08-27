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
/// Which projects to leave answering the phone.
///
/// The desk and the phone want opposite things from a session list. At the desk a session is
/// found by remembering what it was about, so every one you ever worked in is worth keeping.
/// From a phone there is no terminal to open and no folder to <c>cd</c> into: a project you
/// can reach is one that already has a <c>claude rc</c> host serving it, and a project that
/// does not is simply not there. So this reads recency off the transcripts and answers with
/// the folders worth having up before you leave the desk.
///
/// What it puts there is a <c>claude rc</c> <b>host</b>, not a session. The two are different
/// things wearing the same words: <c>claude --remote-control</c> is one interactive session in
/// a terminal that happens to be bridged, while <c>claude rc</c> is a server that pre-creates
/// one session so there is somewhere to type immediately and then spawns more on demand, up to
/// its capacity. A host is the right shape here because the phone is where second thoughts
/// happen — a session per project caps you at one conversation per repo, and starting another
/// is the one thing a phone cannot do for itself.
///
/// Which is also why a bridged session does not count as one. It was tempting to read the
/// registry and pass over any folder with Remote Control connected — the phone can reach it,
/// after all — but reaching is not the thing standby is for. A folder held only by a
/// <c>claude --remote-control</c> terminal shows exactly one conversation on the phone and no
/// way to open a second, which is the state this verb exists to get you out of. So the only
/// thing that means "already on standby" is a live host, and a bridged terminal in the folder
/// is passed over in silence: it is not in the way, and one extra row is a smaller cost than
/// a repo you cannot start a thought in.
///
/// It is folders, not sessions, all the way down: the host decides what conversations exist,
/// so there was never a resumed-versus-fresh question for this to answer.
///
/// Pure, like <see cref="RestartPolicy"/> and <see cref="ClosePolicy"/>: the caller supplies
/// the scan, the clock and the answers to the few filesystem questions, and gets back a plan
/// it can print without having opened anything.
/// </summary>
public static class Standby
{
    /// <summary>How far back "recently worked on" reaches when the caller does not say.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <param name="sessions">Scanned sessions — only their folder and last turn are read.</param>
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
    /// <param name="hostFor">
    /// The <c>claude rc</c> host serving a project's transcript folder, if one is. The whole
    /// question of "is this folder already on standby", because a host is the only thing that
    /// answers it — the session registry cannot, in either direction: a host publishes no
    /// session of its own, and a session that is bridged is not a host. Asked of the transcript
    /// folder rather than the repo because that is where the pointer is written, and it comes
    /// from the scan itself, so no path has to be slugged to ask it.
    /// </param>
    public static StandbyPlan Decide(
        IEnumerable<SessionInfo> sessions,
        DateTime now,
        TimeSpan? window = null,
        int max = int.MaxValue,
        Func<string, bool>? folderExists = null,
        Func<string, bool>? isRepo = null,
        Func<string, BridgePointer?>? hostFor = null)
    {
        var since = now - (window ?? DefaultWindow);
        var exists = folderExists ?? Directory.Exists;
        var repo = isRepo ?? HasGit;
        var host = hostFor ?? (dir => RemoteControlHosts.ServingFrom(dir));

        var newest = new Dictionary<string, (string Folder, string ProjectDir, DateTime LastActive)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            if (session.RealCwd is not { Length: > 0 } cwd) continue;
            if (session.LastActive < since) continue;
            if (IsScratch(cwd)) continue;

            var key = Key(cwd);
            if (newest.TryGetValue(key, out var seen) && seen.LastActive >= session.LastActive) continue;

            // The transcript folder the session file sits in, which is where a host writes its
            // pointer. Taken from the scan rather than slugged from the path, so the one rule
            // this code would otherwise have to duplicate stays Claude Code's own.
            newest[key] = (cwd, DirectoryOf(session.FilePath), session.LastActive);
        }

        var open = new List<StandbyTarget>();
        var skipped = new List<StandbySkip>();

        foreach (var folder in newest.Values.OrderByDescending(e => e.LastActive))
        {
            var project = ProjectOf(folder.Folder);

            // The only thing that counts as already on standby, because it is the only thing
            // that does what standby is for. Launching a second host is the mistake this verb
            // is most likely to make — it publishes no session of its own, so nothing in the
            // registry would have caught it — and a bridged terminal here is not that mistake.
            if (host(folder.ProjectDir) is { } serving)
            {
                skipped.Add(new StandbySkip
                {
                    Folder = folder.Folder,
                    Project = project,
                    Reason = AlreadyReason(serving),
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

    /// <summary>How a folder a host is already serving is reported.</summary>
    public static string AlreadyReason(BridgePointer serving) =>
        $"already on standby — a claude rc host (pid {serving.Pid}) is serving this folder";

    /// <summary>
    /// The folder a session file sits in, tolerating the empty path a hand-built
    /// <see cref="SessionInfo"/> has.
    /// </summary>
    private static string DirectoryOf(string filePath) =>
        string.IsNullOrEmpty(filePath) ? "" : Path.GetDirectoryName(filePath) ?? "";

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
