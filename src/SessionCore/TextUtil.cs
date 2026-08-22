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
    /// A pause this long means you left the session and came back: the turns either
    /// side of it belong to different sittings. Anything shorter is one stretch of
    /// work with thinking time in it.
    /// </summary>
    public static readonly TimeSpan SittingGap = TimeSpan.FromHours(1);

    /// <summary>
    /// "1h ago" for a session worked on once; "2 days ago -> 1h ago" when there was an
    /// earlier sitting — previous work on the left, latest on the right. Both ends are
    /// real turns: opening a session (or answering Claude Code's restore prompt) writes
    /// no turn, so neither date moves until you actually say something.
    /// </summary>
    public static string AgeDisplay(DateTime lastActive, DateTime? previousActive, DateTime? now = null)
    {
        var age = RelativeAge(lastActive, now);
        return previousActive is { } prev && prev < lastActive
            ? $"{RelativeAge(prev, now)} → {age}"
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
