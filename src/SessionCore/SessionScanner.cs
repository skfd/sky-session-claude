namespace SessionCore;

public sealed record ScanOptions
{
    /// <summary>Show every session in every project, not just the newest one per project.</summary>
    public bool All { get; init; } = true;
    /// <summary>How many entries to return.</summary>
    public int Top { get; init; } = 50;
    /// <summary>Token budget used to compute Ctx%.</summary>
    public int ContextWindow { get; init; } = TranscriptParser.DefaultContextWindow;
}

/// <summary>
/// Scans ~/.claude/projects for transcripts and builds display rows. Faithful
/// port of Get-SessionRows from get-claudesessions.ps1.
/// </summary>
public sealed class SessionScanner
{
    private readonly string _projectsDir;
    private readonly TranscriptCache _cache = new();

    public SessionScanner(string? projectsDir = null)
    {
        _projectsDir = projectsDir ?? DefaultProjectsDir();
    }

    public string ProjectsDir => _projectsDir;

    public static string DefaultProjectsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public bool ProjectsDirExists => Directory.Exists(_projectsDir);

    /// <summary>Enumerate all transcript files, newest first, honoring the All/Top options.</summary>
    public IReadOnlyList<FileInfo> SelectFiles(ScanOptions options)
    {
        if (!Directory.Exists(_projectsDir)) return [];

        var files = new DirectoryInfo(_projectsDir)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .ToList();

        IEnumerable<FileInfo> selected = files;
        if (!options.All)
        {
            // Newest session per project folder.
            selected = files
                .GroupBy(f => f.DirectoryName)
                .Select(g => g.OrderByDescending(f => f.LastWriteTime).First());
        }

        return selected
            .OrderByDescending(f => f.LastWriteTime)
            .Take(options.Top)
            .ToList();
    }

    /// <summary>Parse one file into a full display row.</summary>
    public SessionInfo BuildRow(FileInfo file, int contextWindow)
    {
        TranscriptFields fields;
        try
        {
            fields = _cache.GetOrParse(file, contextWindow);
        }
        catch
        {
            fields = new TranscriptFields();
        }

        var cwd = string.IsNullOrEmpty(fields.Cwd) ? "<unknown - cwd not found in transcript>" : fields.Cwd;

        // "Unfinished" = transcript ends on the agent asking, or on an operator
        // prompt the agent never answered in text.
        bool openQ = !string.IsNullOrEmpty(fields.Recap) && fields.Recap.TrimEnd().EndsWith('?');
        bool noReply = !string.IsNullOrEmpty(fields.LastPrompt) && string.IsNullOrEmpty(fields.Recap);

        return new SessionInfo
        {
            Cwd = cwd,
            Name = string.IsNullOrEmpty(fields.Name) ? "(untitled)" : fields.Name,
            LastPrompt = fields.LastPrompt,
            Recap = fields.Recap,
            Status = fields.Status,
            ContextTokens = fields.ContextTokens,
            ContextPct = fields.ContextPct,
            SessionId = Path.GetFileNameWithoutExtension(file.Name),
            LastActive = file.LastWriteTime,
            AgeDays = Math.Round((DateTime.Now - file.LastWriteTime).TotalDays, 1),
            SizeKB = Math.Round(file.Length / 1024.0, 1),
            Project = LeafOf(cwd),
            Command = $"cd \"{cwd}\"; claude --resume {Path.GetFileNameWithoutExtension(file.Name)}",
            Unfinished = openQ || noReply,
            WaitingOn = noReply ? "agent" : openQ ? "you" : "",
        };
    }

    /// <summary>Full synchronous scan (parity with the original one-shot run).</summary>
    public IReadOnlyList<SessionInfo> Scan(ScanOptions options)
    {
        return SelectFiles(options)
            .Select(f => BuildRow(f, options.ContextWindow))
            .ToList();
    }

    private static string LeafOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }
}
