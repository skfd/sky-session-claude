namespace SessionCore;

public sealed record ScanOptions
{
    /// <summary>Show every session in every project, not just the newest one per project.</summary>
    public bool All { get; init; } = true;
    /// <summary>How many entries to return.</summary>
    public int Top { get; init; } = 50;
    /// <summary>Token budget used to compute Ctx%.</summary>
    public int ContextWindow { get; init; } = SessionFileParser.DefaultContextWindow;
    /// <summary>
    /// Base model id whose sessions ran with the 1M window, read from the "[1m]"
    /// suffix on the configured model in ~/.claude/settings.json; null when no 1M
    /// model is configured.
    /// </summary>
    public string? LargeModelId { get; init; } = ClaudeSettings.ReadLargeModelId();
}

/// <summary>
/// Scans ~/.claude/projects for session files and builds display rows. Faithful
/// port of Get-SessionRows from get-claudesessions.ps1.
/// </summary>
public sealed class SessionScanner
{
    private readonly string _projectsDir;
    private readonly SessionFileCache _cache = new();

    public SessionScanner(string? projectsDir = null)
    {
        _projectsDir = projectsDir ?? DefaultProjectsDir();
    }

    public string ProjectsDir => _projectsDir;

    public static string DefaultProjectsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public bool ProjectsDirExists => Directory.Exists(_projectsDir);

    /// <summary>Enumerate all session files, newest first, honoring the All/Top options.</summary>
    public IReadOnlyList<FileInfo> SelectFiles(ScanOptions options)
    {
        if (!Directory.Exists(_projectsDir)) return [];

        // Real sessions live exactly at <projectsDir>/<flat-project>/<uuid>.jsonl.
        // Anything deeper is per-session auxiliary data — subagent transcripts
        // (<session-id>/subagents/agent-*.jsonl, not resumable and never titled),
        // tool-results, memory — so only the top level of each project counts.
        var files = new DirectoryInfo(_projectsDir)
            .EnumerateDirectories()
            .SelectMany(d => d.EnumerateFiles("*.jsonl"))
            .ToList();

        IEnumerable<FileInfo> selected = files;
        if (!options.All)
        {
            // Newest session per project folder.
            selected = files
                .GroupBy(f => f.DirectoryName)
                .Select(g => g.OrderByDescending(f => f.LastWriteTime).First());
        }

        // Ordered/capped by last-write here because the real last-active time is only
        // known after parsing, and a file's last write can never precede its newest
        // record — so the top-N by last-write is a superset of the top-N by activity.
        // Scan re-sorts on the parsed times.
        return OnePerSession(selected)
            .Take(options.Top)
            .ToList();
    }

    /// <summary>
    /// Every session file whose id starts with <paramref name="prefix"/>, newest first.
    ///
    /// A session id is its file's base name, so finding one is a directory walk and not a
    /// scan — no session file is opened, let alone parsed. That is what lets the CLI act on
    /// a named session in milliseconds instead of paying for a full scan first, and it is
    /// why a prefix works at all: `SessionCli done 4f2a` is the same gesture as a short
    /// commit sha, and the caller decides what to do when it matches more than one.
    /// </summary>
    public IReadOnlyList<FileInfo> FindByPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !Directory.Exists(_projectsDir)) return [];

        // A session id is a uuid; anything a caller could use to escape the directory or
        // turn the prefix into a glob of its own is not one.
        if (prefix.Any(c => c is '*' or '?' or '/' or '\\' or ':')) return [];

        var matches = new DirectoryInfo(_projectsDir)
            .EnumerateDirectories()
            .SelectMany(d => d.EnumerateFiles($"{prefix}*.jsonl"))
            // Windows matches 8.3 short names against a pattern too, so confirm the long
            // name really does start with what was asked for.
            .Where(f => Path.GetFileNameWithoutExtension(f.Name)
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return OnePerSession(matches).ToList();
    }

    /// <summary>
    /// One id, one file, newest first. A conversation that moves between folders — resumed
    /// from somewhere else, or a cwd change part way through — is written to a second
    /// project folder under the same uuid, leaving two transcripts for one session. The id
    /// is what everything downstream keys on: a dictionary of rows, a prefix lookup, a
    /// disposition, a live process. So the copy written most recently answers for the id,
    /// being the one the session is still appending to, and the stragglers are dropped
    /// rather than surfacing as a duplicate key or as an ambiguity no longer prefix could
    /// ever resolve.
    /// </summary>
    private static IEnumerable<FileInfo> OnePerSession(IEnumerable<FileInfo> files) =>
        files
            .OrderByDescending(f => f.LastWriteTime)
            .DistinctBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Parse one file into a full display row.</summary>
    public SessionInfo BuildRow(FileInfo file, int contextWindow, string? largeModelId = null)
    {
        SessionFileFields fields;
        try
        {
            fields = _cache.GetOrParse(file, contextWindow, largeModelId);
        }
        catch
        {
            fields = new SessionFileFields();
        }

        var cwd = string.IsNullOrEmpty(fields.Cwd) ? SessionInfo.UnknownCwd : fields.Cwd;

        // "Unfinished" = session file ends on the agent asking, or on an operator
        // prompt the agent never answered in text.
        bool openQ = !string.IsNullOrEmpty(fields.Recap) && fields.Recap.TrimEnd().EndsWith('?');
        bool noReply = !string.IsNullOrEmpty(fields.LastPrompt) && string.IsNullOrEmpty(fields.Recap);

        var lastActive = LastActiveOf(file, fields);
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);

        return new SessionInfo
        {
            Cwd = cwd,
            // The one point a title is resolved, so a placeholder Sky wrote back into the
            // transcript is refused once rather than in each of display, launch and policy.
            Name = SessionName.RealTitle(fields.CustomTitle, fields.AiTitle, sessionId, fields.Cwd)
                   ?? SessionInfo.Untitled,
            CustomTitle = fields.CustomTitle,
            AiTitle = fields.AiTitle,
            LastPrompt = fields.LastPrompt,
            Recap = fields.Recap,
            Status = fields.Status,
            ContextTokens = fields.ContextTokens,
            ContextPct = fields.ContextPct,
            IsLargeContext = fields.IsLargeContext,
            SessionId = sessionId,
            FilePath = file.FullName,
            LastActive = lastActive,
            LastTouched = file.LastWriteTime,
            PreviousActive = PreviousActiveOf(fields, lastActive),
            AgeDays = Math.Round((DateTime.Now - lastActive).TotalDays, 1),
            SizeKB = Math.Round(file.Length / 1024.0, 1),
            Project = LeafOf(cwd),
            Command = $"cd \"{cwd}\"; {ClaudeLaunch.Resume(sessionId)}",
            Unfinished = openQ || noReply,
            WaitingOn = noReply ? "agent" : openQ ? "you" : "",
        };
    }

    /// <summary>
    /// When the session was last actually worked on. Resuming a session appends
    /// untimestamped metadata records, which bumps the file's last-write time to now
    /// and would otherwise erase how long the session has really been sitting — so the
    /// last real turn in the file wins, and last-write is only the fallback for files
    /// that carry no timestamped turn at all. Floored at the file's creation time so a
    /// freshly written file (a fork, which copies the original's older records) still
    /// reads as new rather than as old as the conversation it branched from.
    /// </summary>
    private static DateTime LastActiveOf(FileInfo file, SessionFileFields fields)
    {
        if (fields.LastTurnUtc is not { } utc) return file.LastWriteTime;
        var turn = utc.ToLocalTime();
        return turn > file.CreationTime ? turn : file.CreationTime;
    }

    /// <summary>
    /// End of the sitting before the current one, dropped when it doesn't sit strictly
    /// behind <paramref name="lastActive"/> — which is how a fork, whose age is floored
    /// at its own creation time, avoids advertising the original's older sittings.
    /// </summary>
    private static DateTime? PreviousActiveOf(SessionFileFields fields, DateTime lastActive)
    {
        if (fields.PreviousSittingUtc is not { } utc) return null;
        var prev = utc.ToLocalTime();
        return prev < lastActive ? prev : null;
    }

    /// <summary>Full synchronous scan (parity with the original one-shot run).</summary>
    public IReadOnlyList<SessionInfo> Scan(ScanOptions options)
    {
        return SelectFiles(options)
            .Select(f => BuildRow(f, options.ContextWindow, options.LargeModelId))
            .OrderByDescending(r => r.LastActive)
            .ToList();
    }

    private static string LeafOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }
}
