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
/// Concurrency, atomicity and the handling of a file that will not read are
/// <see cref="JsonSidecar{TValue}"/>'s, shared with <see cref="DispositionStore"/>: this has
/// the same writers for the same reasons — the app's background pass, <c>SessionCli rename</c>
/// on an agent's behalf, and a session renaming itself.
/// </summary>
public sealed class NameStore
{
    private const string FileName = "names.json";
    private const string MutexPrefix = @"Local\sky-session-claude-names";

    private readonly JsonSidecar<NameRecord> _file;
    private Dictionary<string, NameRecord> _names;

    public NameStore() : this(DispositionStore.DefaultDir()) { }

    public NameStore(string dir)
    {
        _file = new JsonSidecar<NameRecord>(
            Path.Combine(dir, FileName),
            MutexPrefix,
            Serialize,
            Deserialize,
            unreadableNote: "names are not being tracked");

        _names = _file.Load();
    }

    /// <summary>
    /// Set when the last load found something wrong. Worth surfacing: the alternative is Sky
    /// quietly forgetting which names are its own, and treating all of them as yours.
    /// </summary>
    public string? LoadWarning => _file.Warning;

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

        _names = MutateOrKeep(names =>
        {
            var wanted = new NameRecord(name, origin);
            if (names.TryGetValue(sessionId, out var current) && current == wanted) return false;
            names[sessionId] = wanted;
            return true;
        });
    }

    /// <summary>Forget a session, so its current name reads as the operator's from here on.</summary>
    public void Forget(string sessionId) =>
        _names = MutateOrKeep(names => names.Remove(sessionId));

    /// <summary>
    /// Re-read the file if another writer has touched it. One stat when nothing changed,
    /// which is what lets a name written by <c>SessionCli rename</c> reach the app's next
    /// pass instead of that pass deciding the name was the operator's and leaving it alone.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (!_file.ChangedOnDisk) return false;

        var before = _names;
        _names = _file.Load();
        return !JsonSidecar<NameRecord>.Same(before, _names);
    }

    /// <summary>
    /// Apply a change, or keep what we have when the file could not be read at all. Handing
    /// back the empty map a failed read produces would clear in memory the very entries the
    /// write just refused to clear on disk.
    /// </summary>
    private Dictionary<string, NameRecord> MutateOrKeep(Func<Dictionary<string, NameRecord>, bool> apply) =>
        _file.Mutate(apply) ?? _names;

    // --- the file format ----------------------------------------------------

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

    private static string Serialize(Dictionary<string, NameRecord> names) =>
        JsonSerializer.Serialize(
            names
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => new Wire
                {
                    Name = kv.Value.Name,
                    Origin = ToWire(kv.Value.Origin),
                }),
            Wire.Options);

    private static Dictionary<string, NameRecord>? Deserialize(string text)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Wire>>(text, Wire.Options);
        if (raw is null) return null;

        var names = JsonSidecar<NameRecord>.NewMap();
        foreach (var kv in raw)
        {
            if (kv.Value?.Name is not { Length: > 0 } name) continue;
            names[kv.Key] = new NameRecord(name, FromWire(kv.Value.Origin));
        }
        return names;
    }

    public static string ToWire(NameOrigin origin) => origin switch
    {
        NameOrigin.Chosen => "chosen",
        NameOrigin.SelfNamed => "self",
        NameOrigin.Oracle => "oracle",
        NameOrigin.Title => "title",
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
        "oracle" => NameOrigin.Oracle,
        "title" => NameOrigin.Title,
        _ => NameOrigin.Floor,
    };
}
