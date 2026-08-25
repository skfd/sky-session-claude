namespace SessionCore;

/// <summary>
/// A small JSON map on disk that more than one process writes.
///
/// Both sidecars beside the session list work this way — <see cref="DispositionStore"/> for
/// what the operator decided about a session, <see cref="NameStore"/> for which names are
/// Sky's own — and both have the same writers for the same reason: the app on a keystroke or
/// a background pass, and SessionCli on an agent's behalf. What follows from that is not
/// obvious enough to want two copies of, so it lives here once.
///
/// Four rules earn their keep:
/// <list type="bullet">
/// <item>Never write cached state. A write re-reads the file under the lock first, so a
/// change made elsewhere since startup survives.</item>
/// <item>Never truncate in place. The new file is written beside the old one and moved over
/// it, so a crash or a concurrent reader can never see half a file.</item>
/// <item>Never overwrite a file we could not read. Replacing real entries with an empty set
/// is the one failure that destroys something; a store that is there but out of reach is
/// left alone and the warning carries the news.</item>
/// <item>Never silently start fresh. A corrupt file is moved aside and reported, because
/// entries vanishing without a word is worse than losing them loudly.</item>
/// </list>
///
/// The file format stays the caller's business: it hands in the two functions that turn its
/// own map into text and back. That is what keeps both files something you can read and
/// correct by hand, which is half the point of them being JSON at all.
/// </summary>
internal sealed class JsonSidecar<TValue>
{
    /// <summary>A write waits this long for the other writer before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly string _mutexName;
    private readonly Func<Dictionary<string, TValue>, string> _serialize;
    private readonly Func<string, Dictionary<string, TValue>?> _deserialize;
    private readonly Func<Dictionary<string, TValue>>? _whenMissing;
    private readonly string _unreadableNote;

    /// <param name="mutexPrefix">
    /// Local, not Global: every writer runs as the same user on the same desktop, and creating
    /// a Global object needs a privilege a standard account may not hold.
    /// </param>
    /// <param name="whenMissing">
    /// What to start from when there is no file at all — a migration from an older store, say.
    /// Reached only for a genuinely absent file, never as a fallback from one that failed to
    /// parse: answering a corrupt store with a pre-migration default would quietly undo every
    /// change made since.
    /// </param>
    /// <param name="unreadableNote">
    /// What the caller loses while the file cannot be read — "marks are not being saved" —
    /// appended to that warning, because the consequence is what makes it worth reading.
    /// </param>
    public JsonSidecar(
        string path,
        string mutexPrefix,
        Func<Dictionary<string, TValue>, string> serialize,
        Func<string, Dictionary<string, TValue>?> deserialize,
        string unreadableNote,
        Func<Dictionary<string, TValue>>? whenMissing = null)
    {
        _path = path;
        _unreadableNote = unreadableNote;
        _serialize = serialize;
        _deserialize = deserialize;
        _whenMissing = whenMissing;

        // Two stores over two directories (a test, say) must not block each other, and a mutex
        // name may not contain a path separator.
        _mutexName = $"{mutexPrefix}-{Hash(Path.GetFullPath(path))}";
    }

    /// <summary>
    /// Set when the last load or write found something wrong. Worth surfacing: the alternative
    /// is entries silently missing.
    /// </summary>
    public string? Warning { get; private set; }

    /// <summary>Write time of the file the caller's copy came from; drives reloads.</summary>
    public DateTime Stamp { get; private set; }

    /// <summary>True when someone else has written the file since we last looked.</summary>
    public bool ChangedOnDisk => StampOf(_path) != Stamp;

    /// <summary>Read the file, and remember when it was written.</summary>
    public Dictionary<string, TValue> Load()
    {
        var read = Read();
        Warning = read.Warning;
        Stamp = StampOf(_path);
        return Values(read);
    }

    /// <summary>
    /// Take the lock, re-read what is on disk, let <paramref name="apply"/> change it, and
    /// replace the file atomically. What comes back is whatever was on disk plus the change —
    /// never what the caller was holding beforehand.
    /// </summary>
    /// <returns>
    /// Null when nothing was attempted, and the caller should keep the copy it already has.
    /// That is the unreadable case: the file is there but out of reach, so what we read is
    /// empty through no fault of the entries, and handing that back would clear in memory the
    /// very marks we just refused to clear on disk.
    /// </returns>
    public Dictionary<string, TValue>? Mutate(Func<Dictionary<string, TValue>, bool> apply)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool held = false;
        try
        {
            held = Acquire(mutex);

            var read = Read();
            Warning = read.Warning;

            // The one case where writing would destroy something: we would replace real
            // entries with an empty set. Leave the file alone, and say so.
            if (read.Outcome == Outcome.Unreadable) return null;

            var values = Values(read);
            if (apply(values) && Write(values)) Stamp = StampOf(_path);
            return values;
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    /// <summary>Whether two maps hold the same entries, for "did a reload change anything".</summary>
    public static bool Same(Dictionary<string, TValue> a, Dictionary<string, TValue> b) =>
        a.Count == b.Count
        && a.All(kv => b.TryGetValue(kv.Key, out var v) && EqualityComparer<TValue>.Default.Equals(kv.Value, v));

    public static Dictionary<string, TValue> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    // --- the mechanics ------------------------------------------------------

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
    private bool Write(Dictionary<string, TValue> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, _serialize(values));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warning = $"could not write {Path.GetFileName(_path)}: {e.Message}";
            return false;   // the change stays in memory for this run; nothing else to do
        }
    }

    private enum Outcome
    {
        /// <summary>No file yet — a fresh install, or one never migrated.</summary>
        Missing,

        /// <summary>Read cleanly.</summary>
        Loaded,

        /// <summary>Unparseable; moved aside, so starting from empty is safe.</summary>
        Corrupt,

        /// <summary>There but out of reach (locked, denied). Start empty, and never write.</summary>
        Unreadable,
    }

    private readonly record struct Result(
        Outcome Outcome, Dictionary<string, TValue>? Values, string? Warning);

    /// <summary>The entries a read yields — falling back only when there was no file at all.</summary>
    private Dictionary<string, TValue> Values(Result read) =>
        read.Values
        ?? (read.Outcome == Outcome.Missing && _whenMissing is not null ? _whenMissing() : NewMap());

    private Result Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(Outcome.Missing, null, null);

            var values = _deserialize(File.ReadAllText(_path));
            return values is null
                ? new(Outcome.Missing, null, null)
                : new(Outcome.Loaded, values, null);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            // Set it aside rather than overwrite it: the entries may still be recoverable by
            // hand, and an empty store is the least bad way to keep going.
            var aside = _path + ".corrupt";
            try { File.Move(_path, aside, overwrite: true); } catch { /* best effort */ }
            return new(Outcome.Corrupt, null,
                $"{Path.GetFileName(_path)} was unreadable ({e.Message}); "
                + $"moved to {Path.GetFileName(aside)} and started fresh");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(Outcome.Unreadable, null,
                $"could not read {Path.GetFileName(_path)}: {e.Message} — {_unreadableNote}");
        }
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    // Any stable short name will do; this only has to separate one store from another.
    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
