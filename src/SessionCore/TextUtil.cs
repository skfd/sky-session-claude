using System.Text.RegularExpressions;

namespace SessionCore;

public static partial class TextUtil
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Collapse whitespace/newlines into a single clean line (no clipping).</summary>
    public static string FormatLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Whitespace().Replace(text, " ").Trim();

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
