using System.Text.RegularExpressions;

namespace SessionCore;

public static partial class TextUtil
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Collapse whitespace/newlines into a single clean line (no clipping).</summary>
    public static string FormatLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Whitespace().Replace(text, " ").Trim();

    /// <summary>
    /// How far after the last turn a file write counts as a separate visit. During live
    /// work the file is rewritten within seconds of every turn; only a write well after
    /// the fact means someone opened the session and left it alone.
    /// </summary>
    public static readonly TimeSpan VisitGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// "2 days ago" normally; "2 days ago -> 1h ago" when the session was opened again
    /// without producing a turn. The arrow runs from the last real work to that visit,
    /// so a session you reopened and did nothing in still shows how long it has sat.
    /// </summary>
    public static string AgeDisplay(DateTime lastActive, DateTime lastTouched, DateTime? now = null)
    {
        var age = RelativeAge(lastActive, now);
        return lastTouched - lastActive >= VisitGap
            ? $"{age} → {RelativeAge(lastTouched, now)}"
            : age;
    }

    /// <summary>Human-friendly "how long ago", coarsened to the largest useful unit.</summary>
    public static string RelativeAge(DateTime when, DateTime? now = null)
    {
        var span = (now ?? DateTime.Now) - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        var d = (int)span.TotalDays;
        return $"{d} day{(d == 1 ? "" : "s")} ago";
    }
}
