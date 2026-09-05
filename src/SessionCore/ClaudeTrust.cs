using System.Text;
using System.Text.Json;

namespace SessionCore;

/// <summary>What a grant did, and what to say about it.</summary>
public sealed record TrustGrant(bool Ok, string Message);

/// <summary>
/// Whether Claude Code has been trusted with a folder, read from — and written to —
/// <c>~/.claude.json</c>.
///
/// This is the gate standby keeps walking into. <c>claude rc</c> in a folder Claude Code has
/// never been trusted with does not show the "do you trust the files in this folder?" dialog
/// that <see cref="TrustPrompt"/> knows how to answer: it says to run <c>claude</c> there once,
/// and stops. Nothing is written, no session exists, no host appears — the only symptom is an
/// absent host, which is why a sweep of eight untrusted projects used to look like a sweep that
/// worked and leave eight terminals sitting at a prompt.
///
/// The folders in question are the operator's own repos, made by them or by an agent working
/// for them, and never opened interactively — which is exactly how a folder ends up untrusted
/// while being the last thing they worked on. So standby grants the trust it needs as it opens,
/// and says so in the plan first.
///
/// <b>The write is a text edit, not a round trip.</b> <c>~/.claude.json</c> is the operator's
/// whole Claude Code config — every project, every history entry — and re-serialising it to
/// change one boolean would reformat a file this tool does not own and cannot fully model
/// (it holds keys that differ only in case, and objects this code has no schema for). So the
/// property is found in the text and its value replaced, the result is parsed to prove it is
/// still JSON that says what it should, and the original is kept beside it as a backup.
/// </summary>
public static class ClaudeTrust
{
    private const string Flag = "hasTrustDialogAccepted";

    public static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>
    /// Whether <paramref name="folder"/> is trusted: true, false, or null when the config
    /// cannot be read or has never heard of the folder. Null is not "no" — a folder with no
    /// entry has simply never had Claude Code run in it, which is the same practical answer
    /// but a different thing to say.
    /// </summary>
    public static bool? IsTrusted(string folder, string? configPath = null)
    {
        try
        {
            var path = configPath ?? DefaultConfigPath();
            return File.Exists(path) ? ReadTrusted(File.ReadAllText(path), folder) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The same question asked of config text already in hand.</summary>
    public static bool? ReadTrusted(string json, string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Object) return null;

            foreach (var project in projects.EnumerateObject())
            {
                if (!SameFolder(project.Name, folder)) continue;
                return project.Value.ValueKind == JsonValueKind.Object
                    && project.Value.TryGetProperty(Flag, out var flag)
                    && flag.ValueKind == JsonValueKind.True;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Trust <paramref name="folder"/>, keeping a copy of the config first. A folder already
    /// trusted is left alone and said so — the file is not rewritten to say what it says.
    /// </summary>
    public static TrustGrant Grant(string folder, string? configPath = null)
    {
        var path = configPath ?? DefaultConfigPath();

        try
        {
            if (!File.Exists(path))
                return new TrustGrant(false, $"no config at {path} to record trust in");

            var original = File.ReadAllText(path);
            if (ReadTrusted(original, folder) is true)
                return new TrustGrant(true, "already trusted");

            if (!TryGrantIn(original, folder, out var updated, out var why))
                return new TrustGrant(false, why);

            // Beside the file rather than in a temp folder: if anything about this goes wrong
            // the operator wants the copy where they will find it, next to what it copies.
            var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (!File.Exists(backup)) File.Copy(path, backup);

            File.WriteAllText(path, updated);
            return new TrustGrant(true, $"trusted the folder (config backed up to {Path.GetFileName(backup)})");
        }
        catch (Exception ex)
        {
            return new TrustGrant(false, $"could not record trust: {ex.Message}");
        }
    }

    /// <summary>
    /// The edit itself, on text: the flag set to true inside that project's object, or the
    /// flag added to it, or the project added to <c>projects</c> — whichever is missing. Pure,
    /// so the awkward shapes can be tested without a config to ruin.
    /// </summary>
    public static bool TryGrantIn(string json, string folder, out string updated, out string error)
    {
        updated = json;
        error = "";

        string? key = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Object)
            {
                error = "the config has no projects object to record trust in";
                return false;
            }

            foreach (var project in projects.EnumerateObject())
            {
                if (!SameFolder(project.Name, folder)) continue;
                key = project.Name;
                break;
            }
        }
        catch (JsonException ex)
        {
            error = $"the config is not readable as JSON ({ex.Message})";
            return false;
        }

        var edited = key is { Length: > 0 }
            ? SetFlagIn(json, key)
            : AddProject(json, folder);

        if (edited is null)
        {
            error = "could not find where to record it in the config";
            return false;
        }

        // The proof, and the only reason a text edit is safe to make: what comes out is still
        // JSON, and it now answers the question the way it was asked to.
        if (ReadTrusted(edited, folder) is not true)
        {
            error = "the edit did not take — the config is unchanged";
            return false;
        }

        updated = edited;
        return true;
    }

    /// <summary>The flag turned on inside an existing project object, added if it is not there.</summary>
    private static string? SetFlagIn(string json, string key)
    {
        var start = ObjectStart(json, JsonSerializer.Serialize(key));
        if (start < 0) return null;
        var end = ObjectEnd(json, start);
        if (end < 0) return null;

        var body = json[start..end];
        var at = IndexOfProperty(body, Flag);
        if (at >= 0)
        {
            var colon = body.IndexOf(':', at);
            if (colon < 0) return null;
            var valueStart = colon + 1;
            while (valueStart < body.Length && char.IsWhiteSpace(body[valueStart])) valueStart++;
            var valueEnd = valueStart;
            while (valueEnd < body.Length && char.IsLetter(body[valueEnd])) valueEnd++;
            if (body[valueStart..valueEnd] is not ("true" or "false")) return null;
            return json[..start] + body[..valueStart] + "true" + body[valueEnd..] + json[end..];
        }

        // No flag at all: it goes in first, wearing the indentation of whatever follows it.
        var open = body.IndexOf('{');
        if (open < 0) return null;
        var indent = IndentAfter(body, open);
        return json[..start] + body[..(open + 1)] + $"\n{indent}\"{Flag}\": true,"
            + body[(open + 1)..] + json[end..];
    }

    /// <summary>
    /// A folder the config has never heard of, added to <c>projects</c> with the one property
    /// this is about. Claude Code fills in the rest of the entry the first time it runs there.
    /// </summary>
    private static string? AddProject(string json, string folder)
    {
        var start = ObjectStart(json, "\"projects\"");
        if (start < 0) return null;
        var open = json.IndexOf('{', start);
        if (open < 0) return null;

        var indent = IndentAfter(json, open);
        var key = JsonSerializer.Serialize(folder.Replace('\\', '/'));
        var empty = json[(open + 1)..].TrimStart().StartsWith('}');

        return json[..(open + 1)]
            + $"\n{indent}{key}: {{\n{indent}  \"{Flag}\": true\n{indent}}}"
            + (empty ? "\n" : ",")
            + json[(open + 1)..];
    }

    /// <summary>
    /// Where the property named <paramref name="property"/> begins — the name followed by its
    /// colon, so a path that also appears somewhere as a <em>value</em> (the config keeps
    /// histories full of them) cannot be mistaken for the entry that owns it.
    /// </summary>
    private static int ObjectStart(string json, string property)
    {
        for (var at = json.IndexOf(property, StringComparison.Ordinal); at >= 0;
             at = json.IndexOf(property, at + 1, StringComparison.Ordinal))
        {
            var i = at + property.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i < json.Length && json[i] == ':') return at;
        }

        return -1;
    }

    /// <summary>
    /// One past the closing brace of the first object after <paramref name="start"/>, counting
    /// braces and stepping over strings so a path with a brace in it cannot end the object.
    /// </summary>
    private static int ObjectEnd(string json, int start)
    {
        var depth = 0;
        var inString = false;

        for (var i = json.IndexOf('{', start); i >= 0 && i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }

        return -1;
    }

    /// <summary>Where a property of that name sits in this object's own text, not a nested one.</summary>
    private static int IndexOfProperty(string body, string name)
    {
        var needle = $"\"{name}\"";
        var depth = 0;
        var inString = false;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                if (depth == 1 && string.CompareOrdinal(body, i, needle, 0, needle.Length) == 0) return i;
                inString = true;
            }
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
        }

        return -1;
    }

    /// <summary>The indentation the next line uses, so an inserted line wears the same.</summary>
    private static string IndentAfter(string text, int open)
    {
        var line = text.IndexOf('\n', open);
        if (line < 0) return "    ";

        var indent = new StringBuilder();
        for (var i = line + 1; i < text.Length && (text[i] == ' ' || text[i] == '\t'); i++)
            indent.Append(text[i]);

        return indent.Length > 0 ? indent.ToString() : "    ";
    }

    /// <summary>
    /// Whether two spellings name the same folder. The config writes its keys with forward
    /// slashes on this machine and backslashes on others, and Windows does not care about
    /// either that or case — so neither does this.
    /// </summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
