using System.Text.Json;

namespace SessionCore;

/// <summary>
/// What the operator decided about a session — never what the classifier decided
/// (see docs/GLOSSARY.md, "Disposition"). A session has at most one.
/// </summary>
public enum Disposition
{
    /// <summary>No judgment recorded; the Status stands on its own.</summary>
    None,

    /// <summary>"Genuinely unfinished, and I'm not going back to it." (X)</summary>
    Abandoned,

    /// <summary>"Finished — whatever the classifier thinks." (D)</summary>
    Done,
}

/// <summary>
/// Persists dispositions by session id. Kept out of sessions.json, which is a
/// regenerated scan artifact and would erase the marks on every scan.
///
/// The store has more than one writer — the app on a keystroke, and SessionCli on
/// behalf of an agent — so every write is a reload-merge-replace under a machine-local
/// mutex rather than a dump of whatever this process happened to load at startup. The
/// file stays a plain 2 KB JSON map: at this size and write rate the mutex costs nothing,
/// and the file is still something you can read and edit by hand.
///
/// Three rules earn their keep:
/// <list type="bullet">
/// <item>Never write cached state. A write re-reads the file first, so a mark made
/// elsewhere since startup survives.</item>
/// <item>Never truncate in place. The new file is written beside the old one and moved
/// over it, so a crash or a concurrent reader can never see half a file.</item>
/// <item>Never fall back to the legacy store on a parse error. A missing file means "not
/// migrated yet"; a corrupt one means something went wrong, and quietly reverting to the
/// pre-1.9 abandon list would erase every Done mark without saying so. The bad file is
/// set aside and reported instead.</item>
/// </list>
/// </summary>
public sealed class DispositionStore
{
    private const string FileName = "dispositions.json";

    /// <summary>Pre-1.9 store: a bare array of ids, all of them abandoned.</summary>
    private const string LegacyFileName = "abandoned.json";

    private const string MutexPrefix = @"Local\sky-session-claude-dispositions";

    private readonly string _legacyPath;
    private readonly JsonSidecar<Disposition> _file;

    private Dictionary<string, Disposition> _marks;

    public DispositionStore() : this(DefaultDir()) { }

    public DispositionStore(string dir)
    {
        _legacyPath = Path.Combine(dir, LegacyFileName);
        _file = new JsonSidecar<Disposition>(
            Path.Combine(dir, FileName),
            MutexPrefix,
            Serialize,
            Deserialize,
            unreadableNote: "marks are not being saved",
            whenMissing: Migrate);

        _marks = _file.Load();
    }

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sky-session-claude");

    /// <summary>
    /// Set when the last load found something wrong — a corrupt store set aside, a file we
    /// cannot read. Worth surfacing: the alternative is marks silently missing.
    /// </summary>
    public string? LoadWarning => _file.Warning;

    public Disposition Get(string sessionId) =>
        _marks.TryGetValue(sessionId, out var d) ? d : Disposition.None;

    /// <summary>Every mark currently known, for callers that list rather than ask.</summary>
    public IReadOnlyDictionary<string, Disposition> All => _marks;

    /// <summary>
    /// Re-read the file if someone else has written it since we last looked. Cheap enough to
    /// call on every scan — one stat when nothing changed — and it is what lets a mark made by
    /// <c>SessionCli done</c> reach the card without restarting the app. Returns true when the
    /// marks actually changed.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (!_file.ChangedOnDisk) return false;

        var before = _marks;
        _marks = _file.Load();
        return !JsonSidecar<Disposition>.Same(before, _marks);
    }

    public void Set(string sessionId, Disposition disposition) =>
        SetMany(new[] { sessionId }, disposition);

    /// <summary>
    /// Apply one disposition to several sessions in a single reload-merge-replace, so a
    /// twenty-row selection costs one write rather than twenty.
    /// </summary>
    public void SetMany(IEnumerable<string> sessionIds, Disposition disposition)
    {
        var ids = sessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        _marks = MutateOrKeep(marks =>
        {
            bool changed = false;
            foreach (var id in ids)
            {
                var current = marks.TryGetValue(id, out var d) ? d : Disposition.None;
                if (current == disposition) continue;

                if (disposition == Disposition.None) marks.Remove(id);
                else marks[id] = disposition;
                changed = true;
            }
            return changed;
        });
    }

    /// <summary>
    /// Apply a change, or keep what we have when the file could not be read at all. Handing
    /// back the empty map a failed read produces would clear in memory the very entries the
    /// write just refused to clear on disk.
    /// </summary>
    private Dictionary<string, Disposition> MutateOrKeep(Func<Dictionary<string, Disposition>, bool> apply) =>
        _file.Mutate(apply) ?? _marks;

    // --- the file format ----------------------------------------------------

    private static string Serialize(Dictionary<string, Disposition> marks) =>
        JsonSerializer.Serialize(
            marks
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => ToWire(kv.Value)),
            new JsonSerializerOptions { WriteIndented = true });

    private static Dictionary<string, Disposition>? Deserialize(string text)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
        if (raw is null) return null;

        var marks = JsonSidecar<Disposition>.NewMap();
        foreach (var kv in raw)
        {
            var value = FromWire(kv.Value);
            if (value != Disposition.None) marks[kv.Key] = value;
        }
        return marks;
    }

    /// <summary>
    /// One-way migration from the pre-1.9 abandon list. The old file is left on disk
    /// untouched, so a downgrade still finds the crosses it wrote; marks made from here on
    /// live in the new store only. Reached only when there is no new store at all — never as
    /// a fallback from a corrupt one, which would erase every Done tick without a word.
    /// </summary>
    private Dictionary<string, Disposition> Migrate()
    {
        var marks = JsonSidecar<Disposition>.NewMap();
        try
        {
            if (!File.Exists(_legacyPath)) return marks;

            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_legacyPath));
            foreach (var id in ids ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(id)) marks[id] = Disposition.Abandoned;

            return marks;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return marks;
        }
    }

    public static string ToWire(Disposition d) => d switch
    {
        Disposition.Abandoned => "abandoned",
        Disposition.Done => "done",
        _ => "none",
    };

    public static Disposition FromWire(string? s) => s switch
    {
        "abandoned" => Disposition.Abandoned,
        "done" => Disposition.Done,
        _ => Disposition.None,
    };
}
