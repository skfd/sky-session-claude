using System.Globalization;
using System.Text.Json;

namespace SessionCore;

/// <summary>The session-file body fields, before file-level info is attached.</summary>
public sealed record SessionFileFields
{
    public string? Cwd { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// The <c>custom-title</c> record, which is what <c>--name</c> and a rename write. Kept
    /// apart from <see cref="AiTitle"/> because the two have different provenance and only
    /// one of them can be a placeholder Sky put there: collapsing them at parse time is what
    /// let a slug be read back as a title and written again.
    /// </summary>
    public string? CustomTitle { get; init; }

    /// <summary>The model-written <c>ai-title</c> record, generated once and never revisited.</summary>
    public string? AiTitle { get; init; }

    public string LastPrompt { get; init; } = "";
    public string Recap { get; init; } = "";
    public SessionStatus Status { get; init; }
    public int ContextTokens { get; init; }
    public int? ContextPct { get; init; }

    /// <summary>Window Ctx% is computed against (200k normally, 1M when detected).</summary>
    public int EffectiveContextWindow { get; init; }

    /// <summary>True when the session ran with an extended (1M) context window.</summary>
    public bool IsLargeContext { get; init; }

    /// <summary>
    /// UTC timestamp of the last real turn (operator or agent), null when the file
    /// carries none. Resuming a session appends untimestamped metadata records
    /// (mode, atis-latch, last-prompt, titles), so this stays put until real work
    /// happens — unlike the file's last-write time.
    /// </summary>
    public DateTime? LastTurnUtc { get; init; }

    /// <summary>
    /// UTC end of the sitting before the current one — the last turn taken more than
    /// <see cref="TextUtil.SittingGap"/> before work picked up again. Null when the
    /// whole session is one stretch of work.
    /// </summary>
    public DateTime? PreviousSittingUtc { get; init; }

    /// <summary>
    /// The <c>uuid</c> of the last genuine operator prompt, or null when the file carries
    /// none. This is the anchor a declaration expires against (see docs/PROJECT-STATE.md):
    /// an agent that declares its state does so mid-turn and then goes on writing — the tool
    /// call, its result and the closing message are all records after the declaration — so
    /// anchoring to the last <i>turn</i> would expire every declaration the instant it was
    /// made. What falsifies a claim about what happens next is the operator saying something
    /// next, and nothing else.
    ///
    /// Tool results and harness-injected records are not prompts. They are user records and
    /// they are real turns, so they still move <see cref="LastTurnUtc"/>; they are not the
    /// operator speaking, so they leave this alone.
    /// </summary>
    public string? LastPromptUuid { get; init; }
}

/// <summary>
/// Faithful port of Get-SessionInfo from get-claudesessions.ps1: reads a JSONL
/// session file once and extracts cwd, title, last prompt, recap, context tokens,
/// and the 7-state end classifier.
/// </summary>
public static class SessionFileParser
{
    public const int DefaultContextWindow = 200_000;
    public const int LargeContextWindow = 1_000_000;

    public static SessionFileFields Parse(IEnumerable<string> lines, int contextWindow = DefaultContextWindow,
        string? largeModelId = null)
    {
        string? cwd = null, name = null, custom = null, prompt = null;
        string? summary = null, lastText = null, userText = null;

        // Signals for the end-state classifier, tracked from the last real turn.
        string? lastRole = null, lastStop = null, errText = null;
        bool lastSynthetic = false, lastHasTool = false, lastEndsQ = false;
        bool lastToolResult = false, lastInterrupt = false;
        int ctxTokens = 0, maxCtxTokens = 0;
        bool sawLargeModel = false;
        DateTime? lastTurnUtc = null, previousSittingUtc = null;
        string? lastPromptUuid = null;

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
            var recordUtc = ReadTimestampUtc(o);
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
                    var turn = HandleUser(o, ref userText, ref lastRole, ref lastToolResult, ref lastInterrupt);
                    if (turn == UserTurn.Noise) break;
                    Advance(ref lastTurnUtc, ref previousSittingUtc, recordUtc);
                    // Taken in file order rather than by timestamp: the file is append-only,
                    // and what a declaration is measured against is what the transcript ends
                    // with, not which record claims the latest clock reading.
                    if (turn == UserTurn.Prompt && TryGetString(o, "uuid", out var uid) && uid.Length > 0)
                        lastPromptUuid = uid;
                    break;
                case "assistant":
                    HandleAssistant(o, largeModelId, ref lastText, ref errText, ref lastRole, ref lastStop,
                        ref lastSynthetic, ref lastHasTool, ref lastEndsQ, ref ctxTokens, ref maxCtxTokens,
                        ref sawLargeModel);
                    Advance(ref lastTurnUtc, ref previousSittingUtc, recordUtc);
                    break;
            }
        }

        prompt ??= userText;                                   // fall back for older session files
        var recap = summary ?? lastText ?? "";

        var status = Classify(lastRole, lastStop, lastSynthetic, lastHasTool, lastEndsQ,
            lastToolResult, lastInterrupt, errText);

        // Two 1M-window signals: a turn observed above the standard window (a 200k
        // model cannot physically exceed ~200k tokens), or turns that ran on the
        // model the operator configured with the "[1m]" suffix — transcripts strip
        // the suffix, so below the threshold the settings default is the only signal.
        bool isLarge = maxCtxTokens > contextWindow || sawLargeModel;
        int effectiveWindow = isLarge ? LargeContextWindow : contextWindow;

        int? ctxPct = ctxTokens > 0
            ? (int)Math.Round(100.0 * ctxTokens / effectiveWindow, MidpointRounding.AwayFromZero)
            : null;

        return new SessionFileFields
        {
            Cwd = cwd,
            Name = custom ?? name,                             // manual title wins over AI one
            CustomTitle = custom,
            AiTitle = name,
            LastPrompt = TextUtil.FormatLine(prompt),
            Recap = TextUtil.FormatLine(recap),
            Status = status,
            ContextTokens = ctxTokens,
            ContextPct = ctxPct,
            EffectiveContextWindow = effectiveWindow,
            IsLargeContext = isLarge,
            LastTurnUtc = lastTurnUtc,
            PreviousSittingUtc = previousSittingUtc,
            LastPromptUuid = lastPromptUuid,
        };
    }

    /// <summary>What a user record turned out to be.</summary>
    private enum UserTurn
    {
        /// <summary>Tooling-injected; not a turn at all.</summary>
        Noise,

        /// <summary>A real turn, but the harness handing back what a tool returned.</summary>
        ToolResult,

        /// <summary>The operator speaking — the only kind a declaration expires against.</summary>
        Prompt,
    }

    /// <summary>Applies one user record, and says whether it was noise, a tool result or the operator.</summary>
    private static UserTurn HandleUser(JsonElement o, ref string? userText, ref string? lastRole,
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
        if (contentIsString && IsHarnessText(utext)) return UserTurn.Noise;

        if (!string.IsNullOrEmpty(utext)) userText = utext;
        lastRole = "user";
        lastToolResult = hasToolResult;
        lastInterrupt = utext is not null && utext.Contains("[Request interrupted by user");

        // An interrupt counts as the operator: they reached over and stopped it, which is
        // exactly the kind of thing a claim about what happens next does not survive.
        return hasToolResult ? UserTurn.ToolResult : UserTurn.Prompt;
    }

    private static void HandleAssistant(JsonElement o, string? largeModelId, ref string? lastText,
        ref string? errText, ref string? lastRole, ref string? lastStop, ref bool lastSynthetic,
        ref bool lastHasTool, ref bool lastEndsQ, ref int ctxTokens, ref int maxCtxTokens,
        ref bool sawLargeModel)
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
            if (largeModelId is not null && GetString(msg, "model") == largeModelId) sawLargeModel = true;
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
    internal static bool IsHarnessText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        return t.StartsWith("<command-", StringComparison.Ordinal)
            || t.StartsWith("<local-command-", StringComparison.Ordinal)
            || t.StartsWith("<system-reminder>", StringComparison.Ordinal)
            || t.StartsWith("<task-notification>", StringComparison.Ordinal);
    }

    /// <summary>
    /// Fold one turn's timestamp into the running pair. Records are written in order but
    /// can carry near-equal clock readings, so the latest only moves forward; a jump of
    /// more than a sitting's gap closes the previous sitting at the turn before it.
    /// </summary>
    private static void Advance(ref DateTime? latest, ref DateTime? previousSitting, DateTime? candidate)
    {
        if (candidate is not { } c || (latest is { } l && c <= l)) return;
        if (latest is { } prev && c - prev >= TextUtil.SittingGap) previousSitting = prev;
        latest = c;
    }

    private static DateTime? ReadTimestampUtc(JsonElement o) =>
        TryGetString(o, "timestamp", out var raw)
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.UtcDateTime
            : null;

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
