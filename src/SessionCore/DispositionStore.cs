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

    /// <summary>
    /// Local, not Global: every writer runs as the same user in the same session, and
    /// creating a Global object needs a privilege a standard account may not hold.
    /// </summary>
    private const string MutexPrefix = @"Local\sky-session-claude-dispositions";

    /// <summary>A write waits this long for the other writer before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _mutexName;

    private Dictionary<string, Disposition> _marks;

    /// <summary>Write time of the file the in-memory copy came from; drives reloads.</summary>
    private DateTime _stamp;

    public DispositionStore() : this(DefaultDir()) { }

    public DispositionStore(string dir)
    {
        _path = Path.Combine(dir, FileName);
        _legacyPath = Path.Combine(dir, LegacyFileName);

        // Two stores over two directories (a test, say) must not block each other, and a
        // mutex name may not contain a path separator.
        _mutexName = $"{MutexPrefix}-{Hash(Path.GetFullPath(_path))}";

        _marks = NewMarks();
        Reload();
    }

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sky-session-claude");

    /// <summary>
    /// Set when the last load found something wrong — a corrupt store set aside, a file
    /// we cannot read. Worth surfacing: the alternative is marks silently missing.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public Disposition Get(string sessionId) =>
        _marks.TryGetValue(sessionId, out var d) ? d : Disposition.None;

    /// <summary>Every mark currently known, for callers that list rather than ask.</summary>
    public IReadOnlyDictionary<string, Disposition> All => _marks;

    /// <summary>
    /// Re-read the file if someone else has written it since we last looked. Cheap enough
    /// to call on every scan — one stat when nothing changed — and it is what lets a mark
    /// made by <c>SessionCli done</c> reach the card without restarting the app. Returns
    /// true when the marks actually changed.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (StampOf(_path) == _stamp) return false;

        var before = _marks;
        Reload();
        return !SameMarks(before, _marks);
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

        Mutate(marks =>
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

    // --- the write path -----------------------------------------------------

    /// <summary>
    /// Take the lock, re-read what is on disk, let <paramref name="apply"/> change it, and
    /// replace the file atomically. The in-memory copy ends up as whatever came back from
    /// disk plus the change — never what this process was holding beforehand.
    /// </summary>
    private void Mutate(Func<Dictionary<string, Disposition>, bool> apply)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool held = false;
        try
        {
            held = Acquire(mutex);

            var read = ReadFile(_path);
            LoadWarning = read.Warning;

            // A store that exists but cannot be read is the one case where writing would
            // destroy something: we would replace real marks with an empty set. Leave the
            // file alone and let the warning carry the news.
            if (read.Outcome == LoadOutcome.Unreadable) return;

            var marks = MarksFrom(read);
            if (apply(marks) && WriteAtomic(marks))
                _stamp = StampOf(_path);

            _marks = marks;
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    // An abandoned mutex means the other writer died mid-write. We hold the lock either
    // way, and the file it left behind is either the old one or the new one — never a
    // half-written one, because the write is a move.
    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(LockTimeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>
    /// Write beside the file, then move over it. <c>File.Move(overwrite: true)</c> is
    /// MoveFileEx with REPLACE_EXISTING, atomic within a volume — no reader ever sees a
    /// truncated store, and a crash leaves the previous one intact.
    /// </summary>
    private bool WriteAtomic(Dictionary<string, Disposition> marks)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var wire = marks
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => ToWire(kv.Value));

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                wire, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"could not write {Path.GetFileName(_path)}: {e.Message}";
            return false;   // marks stay in memory for this run; nothing else to do
        }
    }

    // --- the read path ------------------------------------------------------

    private void Reload()
    {
        var read = ReadFile(_path);
        LoadWarning = read.Warning;
        _marks = MarksFrom(read);
        _stamp = StampOf(_path);
    }

    /// <summary>
    /// What a load turned up. The distinction that matters is <see cref="Missing"/> versus
    /// everything else: only "there is no store here" may fall through to the legacy
    /// abandon list. A corrupt store means something went wrong, and answering it with the
    /// pre-1.9 marks would erase every Done tick without a word.
    /// </summary>
    private enum LoadOutcome
    {
        /// <summary>No store yet — a fresh install, or one that has never been migrated.</summary>
        Missing,

        /// <summary>Read cleanly.</summary>
        Loaded,

        /// <summary>Unparseable; moved aside, so starting from empty is safe.</summary>
        Corrupt,

        /// <summary>There but out of reach (locked, denied). Start empty, and never write.</summary>
        Unreadable,
    }

    private readonly record struct LoadResult(
        LoadOutcome Outcome, Dictionary<string, Disposition>? Marks, string? Warning);

    /// <summary>The marks a load yields — falling back to the legacy list only when there was no store.</summary>
    private Dictionary<string, Disposition> MarksFrom(LoadResult read) =>
        read.Marks ?? (read.Outcome == LoadOutcome.Missing ? Migrate() : NewMarks());

    private static LoadResult ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(LoadOutcome.Missing, null, null);

            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw is null) return new(LoadOutcome.Missing, null, null);

            var marks = NewMarks();
            foreach (var kv in raw)
            {
                var value = FromWire(kv.Value);
                if (value != Disposition.None) marks[kv.Key] = value;
            }
            return new(LoadOutcome.Loaded, marks, null);
        }
        catch (JsonException e)
        {
            // Set it aside rather than overwrite it: the marks may still be recoverable by
            // hand, and an empty store is the least bad way to keep going.
            var aside = path + ".corrupt";
            try { File.Move(path, aside, overwrite: true); } catch { /* best effort */ }
            return new(LoadOutcome.Corrupt, null,
                $"{Path.GetFileName(path)} was unreadable ({e.Message}); "
                + $"moved to {Path.GetFileName(aside)} and started fresh");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(LoadOutcome.Unreadable, null,
                $"could not read {Path.GetFileName(path)}: {e.Message} — marks are not being saved");
        }
    }

    /// <summary>
    /// One-way migration from the pre-1.9 abandon list. The old file is left on disk
    /// untouched, so a downgrade still finds the crosses it wrote; marks made from here on
    /// live in the new store only. Reached only when there is no new store at all — never
    /// as a fallback from a corrupt one.
    /// </summary>
    private Dictionary<string, Disposition> Migrate()
    {
        var marks = NewMarks();
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

    // --- wire format + helpers ----------------------------------------------

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

    private static Dictionary<string, Disposition> NewMarks() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static bool SameMarks(
        Dictionary<string, Disposition> a, Dictionary<string, Disposition> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // Any stable short name will do; this only has to separate one store from another.
    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
