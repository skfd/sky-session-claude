using System.IO;
using System.Text.Json;

namespace SessionApp;

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
/// </summary>
public sealed class DispositionStore
{
    private const string FileName = "dispositions.json";

    /// <summary>Pre-1.9 store: a bare array of ids, all of them abandoned.</summary>
    private const string LegacyFileName = "abandoned.json";

    private readonly string _path;
    private readonly Dictionary<string, Disposition> _marks;

    public DispositionStore() : this(DefaultDir()) { }

    public DispositionStore(string dir)
    {
        _path = Path.Combine(dir, FileName);
        _marks = Load(_path) ?? LoadLegacy(Path.Combine(dir, LegacyFileName));
    }

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sky-session-claude");

    public Disposition Get(string sessionId) =>
        _marks.TryGetValue(sessionId, out var d) ? d : Disposition.None;

    public void Set(string sessionId, Disposition disposition)
    {
        if (Get(sessionId) == disposition) return;
        if (disposition == Disposition.None) _marks.Remove(sessionId);
        else _marks[sessionId] = disposition;
        Save();
    }

    private static string ToWire(Disposition d) => d switch
    {
        Disposition.Abandoned => "abandoned",
        Disposition.Done => "done",
        _ => "none",
    };

    private static Disposition FromWire(string? s) => s switch
    {
        "abandoned" => Disposition.Abandoned,
        "done" => Disposition.Done,
        _ => Disposition.None,
    };

    // A missing or corrupt store is just "nothing marked yet" — the marks are a
    // convenience, never worth failing startup over. Null means "no store here",
    // which is what sends the caller on to the legacy file.
    private static Dictionary<string, Disposition>? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw is null) return null;
            return raw
                .Select(kv => (kv.Key, Value: FromWire(kv.Value)))
                .Where(kv => kv.Value != Disposition.None)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // One-way migration: the old file is left on disk untouched, so a downgrade still
    // finds the crosses it wrote. Marks made from here on live in the new store only.
    private static Dictionary<string, Disposition> LoadLegacy(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, Disposition>();
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return (ids ?? new List<string>())
                .Distinct()
                .ToDictionary(id => id, _ => Disposition.Abandoned);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, Disposition>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var wire = _marks.ToDictionary(kv => kv.Key, kv => ToWire(kv.Value));
            File.WriteAllText(_path, JsonSerializer.Serialize(
                wire, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Marks stay in memory for this run; nothing else to do.
        }
    }
}
