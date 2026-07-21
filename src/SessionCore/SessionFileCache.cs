using System.Collections.Concurrent;

namespace SessionCore;

/// <summary>
/// Caches parsed session file fields keyed by (path, last-write, size, context window)
/// so unchanged session files are not re-read/re-parsed on every refresh. This is what
/// makes frequent and live refresh cheap.
/// </summary>
public sealed class SessionFileCache
{
    private readonly record struct Key(long WriteTicks, long Length, int ContextWindow);
    private readonly ConcurrentDictionary<string, (Key Key, SessionFileFields Fields)> _cache = new();

    public SessionFileFields GetOrParse(FileInfo file, int contextWindow)
    {
        var key = new Key(file.LastWriteTimeUtc.Ticks, file.Length, contextWindow);
        if (_cache.TryGetValue(file.FullName, out var e) && e.Key == key)
            return e.Fields;

        var fields = SessionFileParser.Parse(File.ReadLines(file.FullName), contextWindow);
        _cache[file.FullName] = (key, fields);
        return fields;
    }

    public int Count => _cache.Count;
}
