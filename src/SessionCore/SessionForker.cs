using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionCore;

/// <summary>
/// One place in a session an operator prompt was sent, offered as "fork from just
/// before this prompt". <see cref="LeafUuid"/> is the record the forked branch ends
/// on — the prompt's parent, i.e. the session state the prompt was typed into.
/// </summary>
public sealed record ForkPoint
{
    public required string LeafUuid { get; init; }
    public required string Prompt { get; init; }
    public DateTime? Timestamp { get; init; }
    /// <summary>1-based position of the prompt within the session.</summary>
    public int Ordinal { get; init; }
}

/// <summary>
/// Forks a session file from an arbitrary point by writing a new session file that
/// contains only the ancestry of the chosen record. Session records form a tree
/// (uuid/parentUuid) and the CLI follows a leaf pointer, so a truncated copy under a
/// fresh session id resumes cleanly with `claude --resume &lt;new-id&gt;`.
///
/// The record format is internal to Claude Code and can shift between versions; the
/// original file is never touched, so the worst a format drift can do is produce a
/// fork that fails to resume and gets deleted.
/// </summary>
public static class SessionForker
{
    /// <summary>Operator prompts usable as fork points, in session order.</summary>
    public static IReadOnlyList<ForkPoint> ListForkPoints(string filePath)
    {
        var records = ReadRecords(filePath);
        var ids = records.Where(r => r.Uuid is not null).Select(r => r.Uuid!).ToHashSet();

        var points = new List<ForkPoint>();
        int ordinal = 0;
        foreach (var r in records)
        {
            if (!IsOperatorPrompt(r.Node, out var prompt)) continue;
            ordinal++;
            // A prompt with no surviving parent has nothing before it to fork from.
            if (r.ParentUuid is null || !ids.Contains(r.ParentUuid)) continue;

            DateTime? ts = null;
            if (r.Node["timestamp"]?.GetValue<string>() is string s
                && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                ts = t.ToLocalTime();

            points.Add(new ForkPoint
            {
                LeafUuid = r.ParentUuid,
                Prompt = TextUtil.FormatLine(prompt),
                Timestamp = ts,
                Ordinal = ordinal,
            });
        }
        return points;
    }

    /// <summary>
    /// Write a fork of <paramref name="filePath"/> truncated at <paramref name="leafUuid"/>
    /// into the same project folder under a fresh session id, and return that id.
    /// </summary>
    /// <param name="name">
    /// What the fork should answer to, written as its own <c>custom-title</c>. Without one a
    /// fork inherits the parent's title and every branch of a session reads identically, which
    /// is the one thing the list cannot afford: forks exist to be told apart.
    /// </param>
    public static string ForkFrom(string filePath, string leafUuid, string? name = null)
    {
        var records = ReadRecords(filePath);
        var byId = new Dictionary<string, Record>();
        foreach (var r in records)
            if (r.Uuid is not null) byId[r.Uuid] = r;

        if (!byId.ContainsKey(leafUuid))
            throw new InvalidOperationException($"Fork point {leafUuid} not found in session file.");

        // Ancestry of the chosen leaf = the conversation path the fork keeps.
        var keep = new HashSet<string>();
        for (var cur = leafUuid; cur is not null && byId.TryGetValue(cur, out var rec); cur = rec.ParentUuid)
            keep.Add(cur);

        var newId = Guid.NewGuid().ToString();
        string? lastPrompt = null;
        var outLines = new List<string>();
        foreach (var r in records)
        {
            if (!Keep(r, keep)) continue;
            if (IsOperatorPrompt(r.Node, out var p)) lastPrompt = TextUtil.FormatLine(p);
            if (r.Node["sessionId"] is not null) r.Node["sessionId"] = newId;
            if (r.Node["session_id"] is not null) r.Node["session_id"] = newId;
            outLines.Add(r.Node.ToJsonString());
        }

        // Last, so it outranks anything carried over: SessionFileParser takes the final
        // custom-title. Written into a file this function is authoring anyway, which is why
        // naming a fork needs no authority naming a live session would.
        if (name is { Length: > 0 })
            outLines.Add(new JsonObject
            {
                ["type"] = "custom-title",
                ["customTitle"] = name,
                ["sessionId"] = newId,
            }.ToJsonString());

        // The CLI resumes the branch this pointer names, mirroring real last-prompt records.
        outLines.Add(new JsonObject
        {
            ["type"] = "last-prompt",
            ["lastPrompt"] = lastPrompt ?? "",
            ["leafUuid"] = leafUuid,
            ["sessionId"] = newId,
        }.ToJsonString());

        var dest = Path.Combine(Path.GetDirectoryName(filePath)!, newId + ".jsonl");
        File.WriteAllLines(dest, outLines);
        return newId;
    }

    private static bool Keep(Record r, HashSet<string> keep)
    {
        // Records on the kept path stay; other uuid records (later turns, other
        // branches, sidechains) go.
        if (r.Uuid is not null) return keep.Contains(r.Uuid);

        // Of the uuid-less metadata, keep modes and titles; a compaction summary only
        // if its leaf survived. Snapshots and last-prompt pointers reference records
        // that may be gone — drop them (we append our own leaf pointer).
        var type = r.Node["type"]?.GetValue<string>();
        if (type is "mode" or "permission-mode" or "ai-title" or "custom-title") return true;
        if (type is "summary")
        {
            var leaf = r.Node["leafUuid"]?.GetValue<string>();
            return leaf is null || keep.Contains(leaf);
        }
        return false;
    }

    /// <summary>True for a genuine operator prompt: a user record that is not a
    /// harness injection, tool result, meta record, sidechain turn, or interrupt.</summary>
    private static bool IsOperatorPrompt(JsonObject o, out string prompt)
    {
        prompt = "";
        if (o["type"]?.GetValue<string>() != "user") return false;
        if (o["isMeta"]?.GetValue<bool>() == true) return false;
        if (o["isSidechain"]?.GetValue<bool>() == true) return false;
        if (o["message"] is not JsonObject msg) return false;

        string? text = null;
        bool isString = false;
        switch (msg["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                text = s;
                isString = true;
                break;
            case JsonArray arr:
                foreach (var item in arr.OfType<JsonObject>())
                {
                    var it = item["type"]?.GetValue<string>();
                    if (it == "tool_result") return false;
                    if (it == "text" && item["text"]?.GetValue<string>() is string tx && tx.Length > 0)
                        text = tx;
                }
                break;
        }

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (isString && SessionFileParser.IsHarnessText(text)) return false;
        if (text.Contains("[Request interrupted by user")) return false;

        prompt = text;
        return true;
    }

    private readonly record struct Record(JsonObject Node, string? Uuid, string? ParentUuid);

    private static List<Record> ReadRecords(string filePath)
    {
        var list = new List<Record>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? node;
            try { node = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (node is null) continue;

            list.Add(new Record(node,
                node["uuid"]?.GetValue<string>(),
                node["parentUuid"]?.GetValue<string>()));
        }
        return list;
    }
}
