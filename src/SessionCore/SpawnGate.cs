using System.Globalization;
using System.Text.Json;

namespace SessionCore;

/// <summary>
/// What a <c>PreToolUse</c> hook on <c>Agent</c>/<c>Workflow</c> says about a fan-out when the
/// account is close to a usage limit.
///
/// In the week to 2026-11-08 about 54 subagents died mid-task on "You've hit your 5-hour
/// limit": paid for, then done again. The gate does not make agents cheaper; it stops new ones
/// launching into a wall. Below <c>warn</c> it says nothing. From <c>warn</c> it lets the spawn
/// through with a line for the agent and the operator. From <c>deny</c> it refuses, and tells
/// the agent to do the work inline or stop.
///
/// The usage figures come from <c>~/.claude/widget-data.json</c>, the status line's input
/// that <c>statusline.sh</c> writes on every refresh of any session. Its <c>rate_limits</c>
/// are account-wide, so the last session to refresh speaks for all of them. A window whose
/// <c>resets_at</c> has passed has reset, and counts as empty. A stale file can only
/// under-report, so the gate errs towards letting work through, never towards blocking it.
/// </summary>
public static class SpawnGate
{
    public const int DefaultWarn = 85;
    public const int DefaultDeny = 95;

    /// <summary>The tools that start agents. <c>Task</c> is the older name for <c>Agent</c>.</summary>
    public static bool Gates(string? toolName) =>
        toolName is "Agent" or "Task" or "Workflow";

    public sealed record Window(string Name, int UsedPercent, DateTimeOffset? ResetsAt);

    /// <summary>
    /// The windows still running at <paramref name="now"/>, read off the status line's JSON.
    /// Never throws; a payload it cannot read yields no windows.
    /// </summary>
    public static IReadOnlyList<Window> ReadWindows(string? widgetJson, DateTimeOffset now)
    {
        var list = new List<Window>();
        if (string.IsNullOrWhiteSpace(widgetJson)) return list;

        try
        {
            using var doc = JsonDocument.Parse(widgetJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object) return list;

            foreach (var (key, name) in new[] { ("five_hour", "5-hour"), ("seven_day", "weekly") })
            {
                if (!limits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("used_percentage", out var p) || p.ValueKind != JsonValueKind.Number) continue;

                DateTimeOffset? resets = null;
                if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number)
                    resets = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64());

                if (resets is { } at && at <= now) continue;
                list.Add(new Window(name, (int)Math.Round(p.GetDouble()), resets));
            }
        }
        catch (JsonException)
        {
            list.Clear();
        }

        return list;
    }

    /// <summary>
    /// The hook's whole answer: the JSON to print, or null to print nothing and let the spawn
    /// through. Never throws.
    /// </summary>
    public static string? Answer(string? hookStdin, string? widgetJson, DateTimeOffset now,
        TimeZoneInfo zone, int warn = DefaultWarn, int deny = DefaultDeny)
    {
        if (string.IsNullOrWhiteSpace(hookStdin)) return null;

        string? tool;
        try
        {
            using var doc = JsonDocument.Parse(hookStdin);
            tool = doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("tool_name", out var t)
                   && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (!Gates(tool)) return null;

        var worst = ReadWindows(widgetJson, now).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        if (worst is null || worst.UsedPercent < warn) return null;

        var when = worst.ResetsAt is { } at
            ? " (resets " + TimeZoneInfo.ConvertTime(at, zone).ToString("ddd h:mm tt", CultureInfo.InvariantCulture) + ")"
            : "";
        var where = $"The {worst.Name} usage limit is at {worst.UsedPercent}%{when}.";

        if (worst.UsedPercent >= deny)
        {
            return JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = "deny",
                    permissionDecisionReason =
                        $"Sky spawn gate: {where} New agents now are likely to die mid-task on the limit, "
                        + "paid for and lost. Do this work inline in this session, in small steps, or stop "
                        + "and tell the operator what is left. Do not retry the spawn.",
                },
                systemMessage = $"Sky spawn gate refused to start agents ({tool}): {where}",
            });
        }

        return JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                additionalContext =
                    $"Sky spawn gate: {where} Spawning is still allowed, but keep this fan-out small, "
                    + $"and expect spawns to be refused from {deny}%.",
            },
            systemMessage = $"Sky spawn gate: {where} Fan-outs are refused from {deny}%.",
        });
    }
}
