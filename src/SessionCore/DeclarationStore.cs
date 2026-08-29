using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionCore;

/// <summary>
/// What a session said about itself — never what the operator decided, which is
/// <see cref="DispositionStore"/>, and never what the classifier decided, which is
/// <see cref="SessionStatus"/>. A session has at most one live declaration.
///
/// A copy of <see cref="DispositionStore"/> rather than an invention, for the same reason and
/// with the same discipline: more than one writer — an agent's <c>SessionCli state</c>, and
/// the app if it ever grows a way to say this — so every write is a reload-merge-replace under
/// a machine-local mutex, the new file is moved over the old one rather than truncating it,
/// and a store that will not parse is set aside and reported instead of being silently
/// answered with an empty one.
///
/// Kept out of <c>sessions.json</c>, which every scan regenerates and would erase.
///
/// The one thing it does that the disposition store does not: entries expire. A declaration
/// records the operator prompt it was made at, and <see cref="Declaration.StillStands"/> is
/// what decides whether it is still worth reading — the store itself keeps whatever it was
/// given. Nothing prunes: a stale entry costs a line of JSON and becomes live again on nothing,
/// while pruning would need a scan the store has no business running.
/// </summary>
public sealed class DeclarationStore
{
    private const string FileName = "declarations.json";
    private const string MutexPrefix = @"Local\sky-session-claude-declarations";

    private readonly JsonSidecar<Declaration> _file;

    private Dictionary<string, Declaration> _claims;

    public DeclarationStore() : this(DispositionStore.DefaultDir()) { }

    public DeclarationStore(string dir)
    {
        _file = new JsonSidecar<Declaration>(
            Path.Combine(dir, FileName),
            MutexPrefix,
            Serialize,
            Deserialize,
            unreadableNote: "declarations are not being saved");

        _claims = _file.Load();
    }

    /// <summary>
    /// Set when the last load found something wrong — a corrupt store set aside, a file we
    /// cannot read. Worth surfacing: the alternative is claims silently missing.
    /// </summary>
    public string? LoadWarning => _file.Warning;

    /// <summary>
    /// Whatever was declared about a session, live or expired. Expiry is
    /// <see cref="Declaration.StillStands"/>, which needs the session file this store has
    /// never opened — so this answers with the claim and lets the caller judge it.
    /// </summary>
    public Declaration? Get(string sessionId) =>
        _claims.TryGetValue(sessionId, out var d) ? d : null;

    /// <summary>Every claim currently held, for callers that list rather than ask.</summary>
    public IReadOnlyDictionary<string, Declaration> All => _claims;

    /// <summary>
    /// Re-read the file if someone else has written it since we last looked. Cheap enough to
    /// call on every scan — one stat when nothing changed. Returns true when the claims
    /// actually changed.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (!_file.ChangedOnDisk) return false;

        var before = _claims;
        _claims = _file.Load();
        return !JsonSidecar<Declaration>.Same(before, _claims);
    }

    public void Set(string sessionId, Declaration declaration)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        _claims = MutateOrKeep(claims =>
        {
            if (claims.TryGetValue(sessionId, out var current) && current == declaration) return false;
            claims[sessionId] = declaration;
            return true;
        });
    }

    /// <summary>Take back a claim entirely, for a session that turned out to have more to do.</summary>
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        _claims = MutateOrKeep(claims => claims.Remove(sessionId));
    }

    /// <summary>
    /// Apply a change, or keep what we have when the file could not be read at all. Handing
    /// back the empty map a failed read produces would clear in memory the very entries the
    /// write just refused to clear on disk.
    /// </summary>
    private Dictionary<string, Declaration> MutateOrKeep(Func<Dictionary<string, Declaration>, bool> apply) =>
        _file.Mutate(apply) ?? _claims;

    // --- the file format ----------------------------------------------------

    /// <summary>
    /// One entry as it sits on disk. Its own shape rather than serializing
    /// <see cref="Declaration"/> directly, so the wire names stay a decision — the state is
    /// the same word the CLI takes, not whatever the enum member happens to be called.
    /// </summary>
    private sealed record Entry
    {
        public string State { get; init; } = "none";
        public string? Note { get; init; }
        public string? AtTurn { get; init; }
        public DateTimeOffset At { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Serialize(Dictionary<string, Declaration> claims) =>
        JsonSerializer.Serialize(
            claims
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => new Entry
                {
                    State = ProjectFold.ToWire(kv.Value.State),
                    Note = kv.Value.Note,
                    AtTurn = kv.Value.AtTurn,
                    At = kv.Value.At,
                }),
            Options);

    private static Dictionary<string, Declaration>? Deserialize(string text)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(text, Options);
        if (raw is null) return null;

        var claims = JsonSidecar<Declaration>.NewMap();
        foreach (var kv in raw)
        {
            // A word we no longer understand — a state from a later build, a hand-edit — is
            // dropped rather than kept as None. An entry that decides nothing is worse than
            // no entry: it reads as a session that reported, when nothing was reported.
            var state = ProjectFold.FromWire(kv.Value.State);
            if (state == Declared.None) continue;

            claims[kv.Key] = new Declaration
            {
                State = state,
                Note = string.IsNullOrWhiteSpace(kv.Value.Note) ? null : kv.Value.Note,
                AtTurn = string.IsNullOrWhiteSpace(kv.Value.AtTurn) ? null : kv.Value.AtTurn,
                At = kv.Value.At,
            };
        }
        return claims;
    }
}
