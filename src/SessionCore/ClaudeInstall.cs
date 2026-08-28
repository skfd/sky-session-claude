namespace SessionCore;

/// <summary>
/// Which CLI build is installed, so a live session can be told it is running an old one.
///
/// Claude Code updates in place and the running process keeps the build it started with
/// until it restarts — which is why a dozen terminals all start asking at once. The
/// installed builds sit as one file per version under
/// <c>~/.local/share/claude/versions/</c>, so the newest name there is the answer
/// without paying for a <c>claude --version</c> process spawn.
/// </summary>
public static class ClaudeInstall
{
    public static string DefaultVersionsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "claude", "versions");

    private static readonly Lazy<string?> Installed = new(() => NewestVersion(DefaultVersionsDir()));

    /// <summary>
    /// The newest installed build, read once per run. Null when the versions directory is
    /// absent (a package-manager install elsewhere), and every staleness question then
    /// answers "don't know" rather than guessing.
    /// </summary>
    public static string? InstalledVersion => Installed.Value;

    /// <summary>The highest version-shaped entry name in <paramref name="versionsDir"/>.</summary>
    public static string? NewestVersion(string versionsDir)
    {
        if (!Directory.Exists(versionsDir)) return null;

        string? best = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(versionsDir))
        {
            var name = Path.GetFileName(entry);
            if (Parse(name) is null) continue;              // installer scratch files, .DS_Store, ...
            if (best is null || Compare(name, best) > 0) best = name;
        }
        return best;
    }

    /// <summary>
    /// True when <paramref name="running"/> is behind <paramref name="installed"/> — i.e. this
    /// session is the one nagging you to restart. Unknown or unparseable versions are never
    /// reported stale: a wrong "out of date" would push you to restart a session for nothing.
    /// </summary>
    public static bool IsStale(string? running, string? installed) =>
        running is not null && installed is not null
        && Parse(running) is not null && Parse(installed) is not null
        && Compare(running, installed) < 0;

    /// <summary>Numeric compare of dotted versions, so 2.1.240 sorts above 2.1.99.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parse(a) ?? Array.Empty<int>();
        var y = Parse(b) ?? Array.Empty<int>();
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int cmp = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>
    /// Whether a process name belongs to a Claude CLI.
    ///
    /// Updating renames the binary that is still running: <c>claude.exe</c> becomes
    /// <c>claude.exe.old.&lt;timestamp&gt;</c> so the new build can take the name, and every
    /// process started before the update reports the renamed image from then on. Matching
    /// only "claude" therefore loses sight of a session at the exact moment it falls behind
    /// — which is the moment this app most needs to see it.
    /// </summary>
    public static bool IsClaudeProcess(string? processName) =>
        processName is not null
        && (processName.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || IsSuperseded(processName));

    /// <summary>
    /// True when an update has renamed this process's image out from under it — the only
    /// staleness signal there is for a harness that publishes no version, which is what a
    /// <c>claude rc</c> host is.
    ///
    /// The rename can only have happened because a newer build arrived after the process
    /// started, so superseded means behind. The converse does not hold — a build installed
    /// some other way leaves the name alone — so this under-reports rather than guesses,
    /// the same way <see cref="IsStale"/> treats a version it cannot parse.
    ///
    /// Read it from <see cref="System.Diagnostics.Process.ProcessName"/>, which reports the
    /// image as it now stands on disk. WMI answers <c>claude.exe</c> for these same processes
    /// and would call every host current.
    /// </summary>
    public static bool IsSuperseded(string? processName) =>
        processName is not null
        && processName.StartsWith("claude.exe.old.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dotted digits only; anything else is not a version we can reason about.</summary>
    private static int[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i])) return null;
            if (numbers[i] < 0) return null;
        }
        return numbers;
    }
}
