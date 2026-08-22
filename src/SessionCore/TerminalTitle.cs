namespace SessionCore;

/// <summary>
/// Matching rule for pairing a running session with the terminal tab showing it.
///
/// Claude Code names its terminal by the conversation topic and prefixes a status
/// glyph — <c>✳ Claude Code</c> while idle, <c>◐ Ongoing work</c> while busy. The
/// glyph is repainted as the session's state changes, so the console title read
/// from the process and the tab title read from the window can disagree on it by a
/// moment. Everything after the glyph is the same string, and that is what
/// identifies the tab.
/// </summary>
public static class TerminalTitle
{
    /// <summary>
    /// The title with any leading status glyph and spacing removed — the part that
    /// actually names the conversation. Empty when there is nothing but a glyph.
    /// </summary>
    public static string Topic(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        int start = 0;
        while (start < title.Length && !char.IsLetterOrDigit(title[start])) start++;

        return start == title.Length ? "" : title[start..].Trim();
    }

    /// <summary>
    /// True when two titles name the same session. A title that is only a glyph
    /// matches nothing — better to fall back to focusing the window than to jump to
    /// an arbitrary tab.
    /// </summary>
    public static bool SameSession(string? a, string? b)
    {
        var topic = Topic(a);
        return topic.Length > 0 && topic.Equals(Topic(b), StringComparison.OrdinalIgnoreCase);
    }
}
