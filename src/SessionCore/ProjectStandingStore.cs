using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SessionCore;

/// <summary>
/// What the operator decided about a whole project's life — not what it is waiting on, which
/// is <see cref="ProjectState"/>, worked out fresh on every scan from its sessions.
///
/// The two answer different questions. State is "what does this folder need from me right
/// now"; standing is "is this folder still one I work on at all". A project parked in
/// September can still have a session that declared itself <c>blocked</c> in August, and
/// that claim is true and useless: nobody is going to answer it, on purpose.
///
/// Declared, never derived. In ~/Code the last commit says nothing about whether anyone works
/// on a repo — a sweep that adds a visit counter to thirty map layers makes all thirty look
/// two weeks fresh, and a scheduled job keeps a repo nobody opens committed to daily. Age,
/// sessions and sweeps can propose a standing; only the operator sets one.
/// </summary>
public enum Standing
{
    /// <summary>Worked on, or at least not set aside. Every folder with no entry.</summary>
    Active,

    /// <summary>Set aside on purpose, with a note on what would wake it.</summary>
    Parked,

    /// <summary>Finished. Kept, not worked on.</summary>
    Done,

    /// <summary>
    /// Kept alive by a scheduled job rather than by anyone working in it. Off the list like
    /// parked, but a job that breaks still deserves to be noticed — which is the one way it
    /// differs from done.
    /// </summary>
    Automated,
}

/// <summary>One project's standing, why, and since when.</summary>
public sealed record ProjectStanding
{
    public required Standing Standing { get; init; }

    /// <summary>Why — and for parked, what would wake it. The line the review reads back.</summary>
    public string? Note { get; init; }

    public DateTimeOffset At { get; init; }

    public static string ToWire(Standing s) => s switch
    {
        Standing.Parked => "parked",
        Standing.Done => "done",
        Standing.Automated => "automated",
        _ => "active",
    };

    /// <summary>Null for a word that is not a standing, so a typo is refused rather than read as active.</summary>
    public static Standing? FromWire(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "active" => Standing.Active,
        "parked" or "park" => Standing.Parked,
        "done" => Standing.Done,
        "automated" or "auto" => Standing.Automated,
        _ => null,
    };

    public static readonly string[] Words = ["active", "parked", "done", "automated"];
}

/// <summary>
/// The operator's standings, keyed by project folder. A copy of <see cref="DeclarationStore"/>
/// in its discipline — reload-merge-replace under a mutex, moved over rather than truncated,
/// a corrupt file set aside and reported — because it has the same two writers: the CLI on
/// the operator's or an agent's behalf, and the app if it grows a way to say this.
///
/// Keyed by folder, not by the leaf name, because the fold keys on the folder and two repos
/// called <c>skfd</c> in different roots are two projects. Absent means active: nothing is
/// written for a project until someone sets it aside, so 160 folders cost no lines.
/// </summary>
public sealed class ProjectStandingStore
{
    private const string FileName = "standings.json";
    private const string MutexPrefix = @"Local\sky-session-claude-standings";

    private readonly JsonSidecar<ProjectStanding> _file;
    private Dictionary<string, ProjectStanding> _entries;

    public ProjectStandingStore() : this(DispositionStore.DefaultDir()) { }

    public ProjectStandingStore(string dir)
    {
        _file = new JsonSidecar<ProjectStanding>(
            Path.Combine(dir, FileName),
            MutexPrefix,
            Serialize,
            Deserialize,
            unreadableNote: "project standings are not being saved");
        _entries = _file.Load();
    }

    public string? LoadWarning => _file.Warning;

    public IReadOnlyDictionary<string, ProjectStanding> All => _entries;

    /// <summary>The entry for a folder, or null — which means active.</summary>
    public ProjectStanding? Get(string folder) =>
        _entries.TryGetValue(Key(folder), out var s) ? s : null;

    /// <summary>
    /// The file, and the write time of it this instance last loaded or wrote. A reader that
    /// outlives other writers can be checked by it: <c>SessionCli doctor</c> compares it,
    /// through Sky's heartbeat, to the file's own time.
    /// </summary>
    public (string Path, DateTime Stamp) Loaded => (_file.FilePath, _file.Stamp);

    /// <summary>The standing alone, active when nothing was ever written.</summary>
    public Standing Of(string folder) => Get(folder)?.Standing ?? Standing.Active;

    public bool ReloadIfChanged()
    {
        if (!_file.ChangedOnDisk) return false;
        var before = _entries;
        _entries = _file.Load();
        return !JsonSidecar<ProjectStanding>.Same(before, _entries);
    }

    /// <summary>
    /// Set several at once, under one lock and one write — parking twenty experiments is one
    /// change, not twenty. Active removes the entry rather than storing it, so the file only
    /// ever lists what was set aside.
    /// </summary>
    public void Set(IEnumerable<string> folders, ProjectStanding standing)
    {
        var keys = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Key).ToList();
        if (keys.Count == 0) return;

        _entries = _file.Mutate(entries =>
        {
            bool changed = false;
            foreach (var key in keys)
            {
                if (standing.Standing == Standing.Active)
                    changed |= entries.Remove(key);
                else if (!entries.TryGetValue(key, out var current) || current != standing)
                {
                    entries[key] = standing;
                    changed = true;
                }
            }
            return changed;
        }) ?? _entries;
    }

    public void Set(string folder, ProjectStanding standing) => Set([folder], standing);

    /// <summary>How two spellings of the same folder are told to be the same folder.</summary>
    public static string Key(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

    // --- the file format ----------------------------------------------------

    private sealed record Entry
    {
        public string Standing { get; init; } = "active";
        public string? Note { get; init; }
        public DateTimeOffset At { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Serialize(Dictionary<string, ProjectStanding> entries) =>
        JsonSerializer.Serialize(
            entries
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => new Entry
                {
                    Standing = ProjectStanding.ToWire(kv.Value.Standing),
                    Note = kv.Value.Note,
                    At = kv.Value.At,
                }),
            Options);

    private static Dictionary<string, ProjectStanding>? Deserialize(string text)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(text, Options);
        if (raw is null) return null;

        var entries = JsonSidecar<ProjectStanding>.NewMap();
        foreach (var kv in raw)
        {
            // A word we do not know, or a hand-written "active", is dropped: either way the
            // folder reads as active, and keeping the line would only make the file lie.
            if (ProjectStanding.FromWire(kv.Value.Standing) is not { } standing
                || standing == Standing.Active) continue;

            entries[Key(kv.Key)] = new ProjectStanding
            {
                Standing = standing,
                Note = string.IsNullOrWhiteSpace(kv.Value.Note) ? null : kv.Value.Note,
                At = kv.Value.At,
            };
        }
        return entries;
    }
}

/// <summary>
/// Turns what the operator typed — <c>experiment-*</c>, <c>skfd</c>, a path — into folders.
/// Pure: the caller hands in the candidate folders (the link roots' children, the folders
/// sessions ran in), so a test needs no disk.
/// </summary>
public static class StandingTargets
{
    public sealed record Resolution(IReadOnlyList<string> Folders, IReadOnlyList<string> Unmatched);

    /// <param name="patterns">Leaf names with <c>*</c> and <c>?</c>, or paths.</param>
    /// <param name="candidates">Every folder a name may mean.</param>
    /// <param name="isFolder">Whether a path-shaped pattern names a folder that exists.</param>
    public static Resolution Resolve(
        IEnumerable<string> patterns, IEnumerable<string> candidates, Func<string, bool>? isFolder = null)
    {
        var exists = isFolder ?? Directory.Exists;
        var pool = candidates
            .Select(ProjectStandingStore.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();

        foreach (var pattern in patterns)
        {
            var hits = new List<string>();

            // A path names its folder outright, wildcards or not: pointing beats matching.
            if (pattern.Contains('\\') || pattern.Contains('/') || pattern.Contains(':'))
            {
                var full = ProjectStandingStore.Key(Path.GetFullPath(pattern));
                if (exists(full)) hits.Add(full);
            }
            else
            {
                var rx = new Regex(
                    "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                hits.AddRange(pool.Where(f => rx.IsMatch(Standby.ProjectOf(f))));
            }

            if (hits.Count == 0) unmatched.Add(pattern);
            foreach (var h in hits.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(h)) found.Add(h);
        }

        return new Resolution(found, unmatched);
    }
}
