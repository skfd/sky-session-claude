using System.Text.Json;

namespace SessionCli;

/// <summary>
/// The queue file the brief writes and <c>inbox --run</c> executes.
///
/// This is the return path of a channel that until now only ran one way: a scheduled task
/// drops <c>sessions.json</c> into a folder the brief's sandbox can read, the brief reads
/// it, and there it ended — anything you decided about those sessions you had to carry
/// back to the machine yourself. The brief writing into the same folder closes the loop,
/// and it does so without opening a port: the only writers are the sandbox and processes
/// already on this machine, so there is no listener to defend, no token to leak, and
/// nothing for a web page you happen to be browsing to reach.
///
/// The file is written by an agent, by hand, in one shot, with no chance to see an error
/// and try again until tomorrow. So the parser is deliberately forgiving about spelling
/// (<c>action</c>/<c>verb</c>, <c>id</c>/<c>session</c>, an array with no wrapper) and
/// deliberately strict about everything that decides what runs. Guessing a field name
/// wrong costs a re-read; guessing an action wrong costs someone's terminal.
/// </summary>
internal static class InboxFile
{
    /// <summary>
    /// What the queue may ask for. Reading verbs are absent because a queue that reports
    /// is pointless — the brief already has the scan. <c>trust</c> and <c>fork</c> are
    /// absent for stronger reasons: trust is the one answer that must come from the person
    /// sitting there, and a fork writes a new session file that nobody asked to exist.
    /// </summary>
    public static readonly string[] Allowed =
        ["resume", "restart", "done", "undone", "abandon", "restore", "new"];

    /// <summary>A command as written, once the spellings are normalised away.</summary>
    internal sealed record Entry(string Action, string? Id, string? In, string? Name);

    /// <summary>What a file turned out to be: commands to run, or a reason it will not be.</summary>
    internal sealed record Parsed(List<Entry> Commands, DateTimeOffset? IssuedAt, string? Source);

    /// <summary>Raised for a queue file that cannot be run as written.</summary>
    internal sealed class RejectedException(string message) : Exception(message);

    public static Parsed Read(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new RejectedException($"not valid JSON: {e.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;

            // A bare array is what you get when the writer thinks in commands rather than
            // in documents. It is unambiguous, so it is accepted.
            var list = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object => Field(root, "commands", "actions", "queue") is { } c ? c : default,
                _ => throw new RejectedException("expected an object with a \"commands\" array, or an array."),
            };

            if (list.ValueKind != JsonValueKind.Array)
                throw new RejectedException("no \"commands\" array in the file.");

            var commands = new List<Entry>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new RejectedException($"every command must be an object; found {item.ValueKind}.");

                var action = Text(item, "action", "verb", "do")?.Trim().ToLowerInvariant()
                    ?? throw new RejectedException("a command has no \"action\".");

                if (!Allowed.Contains(action))
                    throw new RejectedException(
                        $"\"{action}\" is not something the inbox runs. Allowed: {string.Join(", ", Allowed)}.");

                commands.Add(new Entry(
                    action,
                    Text(item, "id", "sessionId", "session"),
                    Text(item, "in", "folder", "path", "cwd"),
                    Text(item, "name")));
            }

            DateTimeOffset? issued = null;
            if (root.ValueKind == JsonValueKind.Object
                && Text(root, "issuedAt", "generatedAt", "at") is { } stamp
                && DateTimeOffset.TryParse(stamp, out var parsed))
                issued = parsed;

            var source = root.ValueKind == JsonValueKind.Object ? Text(root, "source") : null;
            return new Parsed(commands, issued, source);
        }
    }

    private static JsonElement? Field(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value)) return value;
        return null;
    }

    private static string? Text(JsonElement obj, params string[] names) =>
        Field(obj, names) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>
    /// Where a run's leftovers go. The input is moved rather than deleted — a queue that
    /// did something surprising is worth being able to read afterwards — and the result
    /// lands under a fixed name so the next brief knows where to look without being told.
    /// </summary>
    public static (string Spent, string Result) Paths(string input)
    {
        var full = Path.GetFullPath(input);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        return (Path.Combine(dir, $"{stem}.last.json"), Path.Combine(dir, $"{stem}-result.json"));
    }
}
