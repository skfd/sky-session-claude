using System.Text.Json;

namespace SessionCore;

/// <summary>
/// Where a name came from, best first. The order is a precedence ladder, and
/// <see cref="NamePolicy"/> only ever replaces a name with one from a strictly better
/// source — which is what stops the background pass from being a rename loop.
///
/// This is quality order, not the order the sources are tried in. docs/NAMING.md lists
/// them cheapest-viable-first — self, then <c>aiTitle</c>, then the oracle — because that
/// is the order worth *spending* in. Here <see cref="Oracle"/> sits above <see cref="Title"/>
/// because an <c>aiTitle</c> is written once, in a session's first ten minutes, and never
/// revisited, while an oracle reads the conversation as it stands now. The two rarely meet:
/// <see cref="NamePolicy.WantsOracle"/> refuses to pay for one while a title exists, so this
/// rung only decides what happens when a caller supplies a subject anyway.
/// </summary>
public enum NameOrigin
{
    /// <summary>
    /// The operator's, and not ours to replace. Recorded rather than merely inferred, so a
    /// name you typed that happens to look like a placeholder is still safe from the shape
    /// check in <see cref="SessionName.IsFloor"/>.
    /// </summary>
    Chosen,

    /// <summary>
    /// The session named itself, through <c>rename --self</c>. The only source that knows
    /// what the conversation is doing now, so nothing outranks it but you.
    /// </summary>
    SelfNamed,

    /// <summary>Composed from a subject <c>claude -p</c> was paid to read out of the transcript.</summary>
    Oracle,

    /// <summary>Composed from the <c>aiTitle</c> already in the transcript. Free and offline.</summary>
    Title,

    /// <summary>The floor: <c>repo-XX</c>, which says only where the session ran.</summary>
    Floor,
}

/// <summary>One session's name as Sky last wrote it.</summary>
/// <param name="Name">
/// The exact string written. A record speaks for <em>this name</em>, not for this session:
/// if the registry now shows something else, the operator has renamed it since and the
/// record no longer applies.
/// </param>
public readonly record struct NameRecord(string Name, NameOrigin Origin);

/// <summary>
/// Which names are Sky's own.
///
/// Nothing in the live registry distinguishes them. A name Sky passes under <c>--name</c>,
/// and a rename it sends over a session's pipe, are both written with <c>nameSource</c>
/// absent — byte for byte what the CLI records for a name you typed yourself. Without a
/// record of its own Sky cannot tell a placeholder it invented last week from a name you
/// chose, so it would either overwrite yours or freeze its own forever. Every other decision
/// in docs/NAMING.md sits on top of this file existing.
///
/// The rule that makes it work is that a record speaks for a <em>name</em>, not for a
/// session: it applies only while the stored string still equals the one in the registry.
/// Rename a session yourself after Sky named it and the two diverge, so the record stops
/// applying and the name is yours from then on — no "reset" gesture to remember, and no way
/// for a stale record to license overwriting something you typed.
///
/// The mechanics are <see cref="DispositionStore"/>'s, for the same reasons: more than one
/// writer (the app's background pass, <c>SessionCli rename</c> on an agent's behalf, a
/// session renaming itself), so every write is a reload-merge-replace under a machine-local
/// mutex, and the new file is written beside the old one and moved over it so no reader ever
/// sees half of one.
/// </summary>
public sealed class NameStore
{
    private const string FileName = "names.json";

    /// <summary>
    /// Local, not Global: every writer runs as the same user on the same desktop, and
    /// creating a Global object needs a privilege a standard account may not hold.
    /// </summary>
    private const string MutexPrefix = @"Local\sky-session-claude-names";

    /// <summary>A write waits this long for the other writer before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly string _mutexName;

    private Dictionary<string, NameRecord> _names;

    /// <summary>Write time of the file the in-memory copy came from; drives reloads.</summary>
    private DateTime _stamp;

    public NameStore() : this(DispositionStore.DefaultDir()) { }

    public NameStore(string dir)
    {
        _path = Path.Combine(dir, FileName);

        // Two stores over two directories (a test, say) must not block each other, and a
        // mutex name may not contain a path separator.
        _mutexName = $"{MutexPrefix}-{Hash(Path.GetFullPath(_path))}";

        _names = NewNames();
        Reload();
    }

    /// <summary>
    /// Set when the last load found something wrong. Worth surfacing: the alternative is
    /// Sky quietly forgetting which names are its own, and treating all of them as yours.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public IReadOnlyDictionary<string, NameRecord> All => _names;

    /// <summary>What Sky last named this session, or null if it never named it.</summary>
    public NameRecord? Get(string sessionId) =>
        _names.TryGetValue(sessionId, out var r) ? r : null;

    /// <summary>
    /// Whether <paramref name="name"/> is the one Sky wrote, and by which route. Null when
    /// the name is not Sky's — either nothing was ever recorded for the session, or the
    /// record names something else and the operator has renamed it since.
    /// </summary>
    public NameOrigin? OriginOf(string sessionId, string? name) =>
        !string.IsNullOrEmpty(name)
        && Get(sessionId) is { } record
        && string.Equals(record.Name, name, StringComparison.Ordinal)
            ? record.Origin
            : null;

    /// <summary>
    /// Record a name Sky just wrote.
    ///
    /// Every Sky name-write calls this in the same operation that writes the name — a launch
    /// under <c>--name</c>, a rename over the pipe, the app's background pass. A write that
    /// skips it puts back the bug this store exists to fix, because the name then reads as
    /// one the operator chose and nothing will ever improve on it.
    /// </summary>
    public void Record(string sessionId, string name, NameOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name)) return;

        Mutate(names =>
        {
            var wanted = new NameRecord(name, origin);
            if (names.TryGetValue(sessionId, out var current) && current == wanted) return false;
            names[sessionId] = wanted;
            return true;
        });
    }

    /// <summary>Forget a session, so its current name reads as the operator's from here on.</summary>
    public void Forget(string sessionId) =>
        Mutate(names => names.Remove(sessionId));

    /// <summary>
    /// Re-read the file if another writer has touched it. One stat when nothing changed,
    /// which is what lets a name written by <c>SessionCli rename</c> reach the app's next
    /// pass instead of that pass deciding the name was the operator's and leaving it alone.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (StampOf(_path) == _stamp) return false;

        var before = _names;
        Reload();
        return !Same(before, _names);
    }

    // --- the write path -----------------------------------------------------

    /// <summary>
    /// Take the lock, re-read what is on disk, let <paramref name="apply"/> change it, and
    /// replace the file atomically. The in-memory copy ends up as whatever came back from
    /// disk plus the change — never what this process was holding beforehand.
    /// </summary>
    private void Mutate(Func<Dictionary<string, NameRecord>, bool> apply)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool held = false;
        try
        {
            held = Acquire(mutex);

            var read = ReadFile(_path);
            LoadWarning = read.Warning;

            // A store that exists but cannot be read is the one case where writing would
            // destroy something: we would replace real records with an empty set, and every
            // name Sky has ever written would read back as the operator's.
            if (read.Outcome == LoadOutcome.Unreadable) return;

            var names = read.Names ?? NewNames();
            if (apply(names) && WriteAtomic(names))
                _stamp = StampOf(_path);

            _names = names;
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    // An abandoned mutex means the other writer died mid-write. We hold the lock either way,
    // and the file it left behind is either the old one or the new one — never a half-written
    // one, because the write is a move.
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
    private bool WriteAtomic(Dictionary<string, NameRecord> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var wire = names
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => new Wire
                {
                    Name = kv.Value.Name,
                    Origin = ToWire(kv.Value.Origin),
                });

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(wire, Wire.Options));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"could not write {Path.GetFileName(_path)}: {e.Message}";
            return false;   // records stay in memory for this run; nothing else to do
        }
    }

    // --- the read path ------------------------------------------------------

    private void Reload()
    {
        var read = ReadFile(_path);
        LoadWarning = read.Warning;
        _names = read.Names ?? NewNames();
        _stamp = StampOf(_path);
    }

    private enum LoadOutcome
    {
        /// <summary>No store yet — Sky has never named anything on this machine.</summary>
        Missing,

        /// <summary>Read cleanly.</summary>
        Loaded,

        /// <summary>Unparseable; moved aside, so starting from empty is safe.</summary>
        Corrupt,

        /// <summary>There but out of reach (locked, denied). Start empty, and never write.</summary>
        Unreadable,
    }

    private readonly record struct LoadResult(
        LoadOutcome Outcome, Dictionary<string, NameRecord>? Names, string? Warning);

    /// <summary>
    /// The on-disk shape. A named object rather than a bare string, so the file stays
    /// something you can read and correct by hand.
    /// </summary>
    private sealed class Wire
    {
        public string? Name { get; set; }
        public string? Origin { get; set; }

        /// <summary>
        /// Lower-case keys, and read back without caring about case. The file is meant to be
        /// corrected by hand, and nobody hand-writing JSON guesses which words the serializer
        /// happened to capitalise.
        /// </summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
    }

    private static LoadResult ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(LoadOutcome.Missing, null, null);

            var raw = JsonSerializer.Deserialize<Dictionary<string, Wire>>(File.ReadAllText(path), Wire.Options);
            if (raw is null) return new(LoadOutcome.Missing, null, null);

            var names = NewNames();
            foreach (var kv in raw)
            {
                if (kv.Value?.Name is not { Length: > 0 } name) continue;
                names[kv.Key] = new NameRecord(name, FromWire(kv.Value.Origin));
            }
            return new(LoadOutcome.Loaded, names, null);
        }
        catch (JsonException e)
        {
            // Set it aside rather than overwrite it. Losing provenance is not catastrophic —
            // the shape check still recognises placeholders — but it is worth being told.
            var aside = path + ".corrupt";
            try { File.Move(path, aside, overwrite: true); } catch { /* best effort */ }
            return new(LoadOutcome.Corrupt, null,
                $"{Path.GetFileName(path)} was unreadable ({e.Message}); "
                + $"moved to {Path.GetFileName(aside)} and started fresh");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(LoadOutcome.Unreadable, null,
                $"could not read {Path.GetFileName(path)}: {e.Message} — names are not being tracked");
        }
    }

    // --- wire format + helpers ----------------------------------------------

    public static string ToWire(NameOrigin origin) => origin switch
    {
        NameOrigin.Chosen => "chosen",
        NameOrigin.SelfNamed => "self",
        NameOrigin.Title => "title",
        NameOrigin.Oracle => "oracle",
        _ => "floor",
    };

    /// <summary>
    /// An origin we do not recognise reads as <see cref="NameOrigin.Floor"/>, the weakest
    /// rung. A future Sky's better source should not stop this one from improving on it, and
    /// the worst that follows is one avoidable rename.
    /// </summary>
    public static NameOrigin FromWire(string? s) => s switch
    {
        "chosen" => NameOrigin.Chosen,
        "self" => NameOrigin.SelfNamed,
        "title" => NameOrigin.Title,
        "oracle" => NameOrigin.Oracle,
        _ => NameOrigin.Floor,
    };

    private static Dictionary<string, NameRecord> NewNames() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static bool Same(
        Dictionary<string, NameRecord> a, Dictionary<string, NameRecord> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // Any stable short name will do; this only has to separate one store from another.
    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
