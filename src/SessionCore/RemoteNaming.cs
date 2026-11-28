using System.Text.Json;

namespace SessionCore;

/// <summary>
/// The title a session gave itself on claude.ai, read back out of its transcript.
///
/// A conversation the phone started under a <c>claude rc</c> host never names itself the
/// local way: it writes no <c>custom-title</c> and no <c>ai-title</c>, so on its way back up
/// it used to be launched as <c>repo-XX</c>. What it does leave is the
/// <c>set_session_title</c> call it made through the claude-code-remote MCP server, and that
/// title is the one the phone's list shows.
///
/// The tool takes any session's id, so a call only counts when it names this session:
/// <c>"self"</c> (or no id), or this session's own id. Which
/// id is ours comes from either of two records: a <c>bridge-session</c> record (a bridged
/// terminal session, <c>cse_X</c>), or the answer to a <c>get_session</c> call made with no
/// id (<c>{"ccr":{"id":"session_X"}}</c>). The server writes the one id both ways, so the
/// part after the prefix is what gets compared.
/// </summary>
internal sealed class RemoteNaming
{
    private readonly HashSet<string> _selfLookups = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Title, int Seq)> _titles = [];
    private string? _self;

    /// <summary>Takes in one transcript record.</summary>
    public void Observe(JsonElement o, string type, int seq)
    {
        switch (type)
        {
            case "bridge-session":
                if (Key(Str(o, "bridgeSessionId")) is { } bridged) _self ??= bridged;
                break;
            case "assistant":
                foreach (var item in Content(o))
                {
                    if (Str(item, "type") != "tool_use") continue;
                    var name = Str(item, "name");
                    item.TryGetProperty("input", out var input);
                    var target = input.ValueKind == JsonValueKind.Object ? Str(input, "session_id") : "";

                    if (name.EndsWith("__set_session_title", StringComparison.Ordinal)
                        && Str(input, "title").Trim() is { Length: > 0 } title)
                    {
                        // "self", or no id at all, is the tool's own way of saying this one.
                        if (target.Length == 0 || target.Equals("self", StringComparison.OrdinalIgnoreCase))
                            _titles.Add((Self, title, seq));
                        else if (Key(target) is { } id)
                            _titles.Add((id, title, seq));
                    }
                    else if (name.EndsWith("__get_session", StringComparison.Ordinal)
                        && target.Length == 0 && Str(item, "id") is { Length: > 0 } call)
                        _selfLookups.Add(call);
                }
                break;
            case "user":
                if (_self is not null || _selfLookups.Count == 0) break;
                foreach (var item in Content(o))
                {
                    if (Str(item, "type") != "tool_result"
                        || !_selfLookups.Contains(Str(item, "tool_use_id"))) continue;
                    if (OwnIdFrom(item) is { } own) { _self = own; break; }
                }
                break;
        }
    }

    /// <summary>The last title this session gave itself, and where in the file it was set.</summary>
    public (string Title, int Seq)? OwnTitle()
    {
        for (int i = _titles.Count - 1; i >= 0; i--)
            if (_titles[i].Id == Self || (_self is not null && _titles[i].Id == _self))
                return (_titles[i].Title, _titles[i].Seq);
        return null;
    }

    /// <summary>Stands for a call that named no session, which the tool takes as the caller.</summary>
    private const string Self = "\0self";

    private static string? OwnIdFrom(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content)) return null;

        IEnumerable<string> texts = content.ValueKind switch
        {
            JsonValueKind.String => [content.GetString() ?? ""],
            JsonValueKind.Array => content.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.Object && Str(t, "type") == "text")
                .Select(t => Str(t, "text")),
            _ => [],
        };

        foreach (var text in texts)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("ccr", out var ccr)
                    && ccr.ValueKind == JsonValueKind.Object
                    && Key(Str(ccr, "id")) is { } id)
                    return id;
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary><c>cse_X</c> and <c>session_X</c> are one session; X is the part to compare.</summary>
    private static string? Key(string id)
    {
        var cut = id.IndexOf('_');
        var key = cut >= 0 ? id[(cut + 1)..] : id;
        return key.Length > 0 ? key : null;
    }

    private static IEnumerable<JsonElement> Content(JsonElement o) =>
        o.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
        && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string Str(JsonElement o, string prop) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
