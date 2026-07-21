using System.Text.Json;

namespace SessionCore;

/// <summary>The transcript-body fields, before file-level info is attached.</summary>
public sealed record TranscriptFields
{
    public string? Cwd { get; init; }
    public string? Name { get; init; }
    public string LastPrompt { get; init; } = "";
    public string Recap { get; init; } = "";
    public SessionStatus Status { get; init; }
    public int ContextTokens { get; init; }
    public int? ContextPct { get; init; }

    /// <summary>Window Ctx% is computed against (200k normally, 1M when detected).</summary>
    public int EffectiveContextWindow { get; init; }

    /// <summary>True when the session ran with an extended (1M) context window.</summary>
    public bool IsLargeContext { get; init; }
}

/// <summary>
/// Faithful port of Get-SessionInfo from get-claudesessions.ps1: reads a JSONL
/// transcript once and extracts cwd, title, last prompt, recap, context tokens,
/// and the 7-state end classifier.
/// </summary>
public static class TranscriptParser
{
    public const int DefaultContextWindow = 200_000;
    public const int LargeContextWindow = 1_000_000;

    public static TranscriptFields Parse(IEnumerable<string> lines, int contextWindow = DefaultContextWindow)
    {
        string? cwd = null, name = null, custom = null, prompt = null;
        string? summary = null, lastText = null, userText = null;

        // Signals for the end-state classifier, tracked from the last real turn.
        string? lastRole = null, lastStop = null, errText = null;
        bool lastSynthetic = false, lastHasTool = false, lastEndsQ = false;
        bool lastToolResult = false, lastInterrupt = false;
        int ctxTokens = 0, maxCtxTokens = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Cheap pre-filter mirroring the PS regex: skip records that carry none
            // of the fields we read (attachment/mode/snapshot noise).
            if (!line.Contains("\"cwd\"") && !line.Contains("\"aiTitle\"")
                && !line.Contains("\"lastPrompt\"") && !line.Contains("\"type\"")) continue;

            JsonElement o;
            try
            {
                using var doc = JsonDocument.Parse(line);
                o = doc.RootElement.Clone();
            }
            catch { continue; }

            if (cwd is null && TryGetString(o, "cwd", out var c) && c.Length > 0) cwd = c;

            var type = GetString(o, "type");
            switch (type)
            {
                case "ai-title":
                    if (TryGetString(o, "aiTitle", out var at) && at.Length > 0) name = at;
                    break;
                case "custom-title":
                    if (TryGetString(o, "customTitle", out var ct) && ct.Length > 0) custom = ct;
                    break;
                case "last-prompt":
                    if (TryGetString(o, "lastPrompt", out var lp) && lp.Length > 0) prompt = lp;
                    break;
                case "system":
                    if (GetString(o, "subtype") == "away_summary"
                        && TryGetString(o, "content", out var content) && content.Length > 0)
                        summary = content;
                    break;
                case "user":
                    HandleUser(o, ref userText, ref lastRole, ref lastToolResult, ref lastInterrupt);
                    break;
                case "assistant":
                    HandleAssistant(o, ref lastText, ref errText, ref lastRole, ref lastStop,
                        ref lastSynthetic, ref lastHasTool, ref lastEndsQ, ref ctxTokens, ref maxCtxTokens);
                    break;
            }
        }

        prompt ??= userText;                                   // fall back for older transcripts
        var recap = summary ?? lastText ?? "";

        var status = Classify(lastRole, lastStop, lastSynthetic, lastHasTool, lastEndsQ,
            lastToolResult, lastInterrupt, errText);

        // A 200k-window model cannot physically exceed ~200k tokens, so any turn
        // observed above the standard window means the session ran with the 1M window.
        bool isLarge = maxCtxTokens > contextWindow;
        int effectiveWindow = isLarge ? LargeContextWindow : contextWindow;

        int? ctxPct = ctxTokens > 0
            ? (int)Math.Round(100.0 * ctxTokens / effectiveWindow, MidpointRounding.AwayFromZero)
            : null;

        return new TranscriptFields
        {
            Cwd = cwd,
            Name = custom ?? name,                             // manual title wins over AI one
            LastPrompt = TextUtil.FormatLine(prompt),
            Recap = TextUtil.FormatLine(recap),
            Status = status,
            ContextTokens = ctxTokens,
            ContextPct = ctxPct,
            EffectiveContextWindow = effectiveWindow,
            IsLargeContext = isLarge,
        };
    }

    private static void HandleUser(JsonElement o, ref string? userText, ref string? lastRole,
        ref bool lastToolResult, ref bool lastInterrupt)
    {
        string? utext = null;
        bool hasToolResult = false;
        bool contentIsString = false;

        if (o.TryGetProperty("message", out var msg)
            && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                utext = content.GetString();
                contentIsString = true;
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var it = GetString(item, "type");
                    if (it == "text" && TryGetString(item, "text", out var tx) && tx.Length > 0) utext = tx;
                    if (it == "tool_result") hasToolResult = true;
                }
            }
        }

        // Harness turns are tooling-injected user records (/clear, <system-reminder>,
        // <task-notification>, local-command wrappers), not the operator speaking.
        // Skip them as noise so the last real turn stays the last genuine operator or
        // agent exchange, instead of misreading an injected record as waiting-agent.
        // These are always plain-string records; a real prompt carrying a trailing
        // reminder comes through as an array, so it is never caught here.
        if (contentIsString && IsHarnessText(utext)) return;

        if (!string.IsNullOrEmpty(utext)) userText = utext;
        lastRole = "user";
        lastToolResult = hasToolResult;
        lastInterrupt = utext is not null && utext.Contains("[Request interrupted by user");
    }

    private static void HandleAssistant(JsonElement o, ref string? lastText, ref string? errText,
        ref string? lastRole, ref string? lastStop, ref bool lastSynthetic, ref bool lastHasTool,
        ref bool lastEndsQ, ref int ctxTokens, ref int maxCtxTokens)
    {
        if (!o.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
        {
            lastRole = "assistant";
            return;
        }

        string? text = null;
        bool hasTool = false;
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var it = GetString(item, "type");
                if (it == "text" && TryGetString(item, "text", out var tx) && tx.Length > 0) text = tx;
                if (it == "tool_use") hasTool = true;
            }
        }

        bool synthetic = GetString(msg, "model") == "<synthetic>"
            || (o.TryGetProperty("isApiErrorMessage", out var err) && err.ValueKind == JsonValueKind.True);

        lastRole = "assistant";
        lastStop = GetString(msg, "stop_reason");
        lastSynthetic = synthetic;
        lastHasTool = hasTool;

        if (synthetic)
        {
            errText = text;                                    // keep for classifying, not for the recap
        }
        else
        {
            if (text is not null) { lastText = text; lastEndsQ = text.TrimEnd().EndsWith('?'); }
            if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                int sum = GetInt(u, "input_tokens")
                        + GetInt(u, "cache_creation_input_tokens")
                        + GetInt(u, "cache_read_input_tokens");
                if (sum > 0) ctxTokens = sum;                  // last real turn wins (resets on compaction)
                if (sum > maxCtxTokens) maxCtxTokens = sum;    // peak drives 1M-window detection
            }
        }
    }

    private static SessionStatus Classify(string? lastRole, string? lastStop, bool lastSynthetic,
        bool lastHasTool, bool lastEndsQ, bool lastToolResult, bool lastInterrupt, string? errText)
    {
        if (lastSynthetic)
        {
            var low = (errText ?? "").ToLowerInvariant();
            return low.Contains("spend limit") || low.Contains("session limit")
                || low.Contains("weekly") || low.Contains("usage limit")
                ? SessionStatus.Limit : SessionStatus.Error;
        }

        if (lastRole == "assistant")
        {
            if (lastHasTool && lastStop == "tool_use") return SessionStatus.CutOff;
            if (lastStop == "max_tokens") return SessionStatus.CutOff;
            if (lastEndsQ) return SessionStatus.WaitingYou;
            return SessionStatus.Complete;
        }

        if (lastRole == "user")
        {
            if (lastInterrupt) return SessionStatus.Interrupted;
            if (lastToolResult) return SessionStatus.CutOff;   // died between tool result and next agent turn
            return SessionStatus.WaitingAgent;
        }

        return SessionStatus.Complete;
    }

    /// <summary>True when a plain-string user record is a tooling-injected harness turn.</summary>
    private static bool IsHarnessText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("<command-", StringComparison.Ordinal)
            || t.StartsWith("<local-command-", StringComparison.Ordinal)
            || t.StartsWith("<system-reminder>", StringComparison.Ordinal)
            || t.StartsWith("<task-notification>", StringComparison.Ordinal);
    }

    // --- small JSON helpers --------------------------------------------------
    private static string GetString(JsonElement o, string prop) =>
        o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool TryGetString(JsonElement o, string prop, out string value)
    {
        if (o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static int GetInt(JsonElement o, string prop) =>
        o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
