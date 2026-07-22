using System.IO;
using System.Text.Json;

namespace SessionApp;

/// <summary>
/// Persists the set of abandoned session ids (see docs/GLOSSARY.md, "Disposition").
/// Kept out of sessions.json, which is a regenerated scan artifact and would erase
/// the marks on every scan.
/// </summary>
public sealed class AbandonedStore
{
    private readonly string _path;
    private readonly HashSet<string> _ids;

    public AbandonedStore() : this(DefaultPath()) { }

    public AbandonedStore(string path)
    {
        _path = path;
        _ids = Load(path);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "sky-session-claude", "abandoned.json");

    public bool Contains(string sessionId) => _ids.Contains(sessionId);

    public void Set(string sessionId, bool abandoned)
    {
        if (abandoned ? _ids.Add(sessionId) : _ids.Remove(sessionId)) Save();
    }

    private static HashSet<string> Load(string path)
    {
        // A missing or corrupt store is just "nothing abandoned yet" — the marks are
        // a convenience, never worth failing startup over.
        try
        {
            if (!File.Exists(path)) return new HashSet<string>();
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new HashSet<string>(ids ?? new List<string>());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_ids.ToList()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Marks stay in memory for this run; nothing else to do.
        }
    }
}
