namespace SessionCore;

/// <summary>
/// What to call a session when we relaunch it on the operator's behalf.
///
/// The CLI names a session from its folder at launch — <c>ontario-address-changes-6c</c> —
/// and never revisits it: a session whose topic has been "Comentality content prototypes"
/// for a week still answers to <c>comentality-com-63</c>. The two hex characters are a
/// collision suffix, drawn fresh each launch, so the one part of the name that changes
/// across a restart is the part that carries no meaning. That churn is what makes a
/// restarted session hard to find again on a phone.
///
/// So a relaunch supplies the name rather than letting it be re-derived. The conversation
/// already has a good one — the model-written title the app shows on its cards — and where
/// there is no title yet (a terminal opened and never used) the folder is genuinely the
/// most that can be said, paired with the session id so it at least stays put.
/// </summary>
public static class SessionName
{
    /// <summary>
    /// Long enough for the titles the CLI writes ("Add death age slider with mortality
    /// visualization" is 48), short enough to read as one line on a phone.
    /// </summary>
    public const int MaxLength = 60;

    /// <summary>
    /// True when the name on a live session is one the operator chose, and therefore not
    /// ours to replace.
    ///
    /// The registry only records <c>nameSource</c> when the CLI invented the name
    /// (<c>derived</c> from the folder, or <c>collision</c> after yielding a claimed one).
    /// A name that came from <c>--name</c> or <c>--remote-control &lt;name&gt;</c> is written
    /// with the field omitted, so "chosen" is the *absence* of a source, not any particular
    /// value of it.
    /// </summary>
    public static bool IsChosen(LiveSession live) =>
        live.Name is { Length: > 0 } && string.IsNullOrEmpty(live.NameSource);

    /// <summary>
    /// The name to launch <paramref name="sessionId"/> under: its title if it has earned
    /// one, otherwise its folder and the id prefix that identifies it everywhere else.
    /// </summary>
    public static string For(string sessionId, string? cwd, string? title)
    {
        if (Tidy(title) is { Length: > 0 } named) return named;

        var folder = Slug(FolderOf(cwd));
        var suffix = Suffix(sessionId);

        if (folder.Length == 0) return suffix.Length > 0 ? $"session-{suffix}" : "session";
        return suffix.Length > 0 ? $"{folder}-{suffix}" : folder;
    }

    /// <summary>Single-quoted for the PowerShell line the name is typed into.</summary>
    public static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// A title as a name: one line, no runaway length. Kept in the words the model chose
    /// rather than slugged — this is read by a person, and "Art persona website design"
    /// beats "art-persona-website-design" on the one surface that shows it.
    /// </summary>
    private static string Tidy(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        var text = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= MaxLength) return text;

        // Cut on a word boundary; an ellipsis in a name is noise, so there is none.
        int cut = text.LastIndexOf(' ', MaxLength);
        return (cut > MaxLength / 2 ? text[..cut] : text[..MaxLength]).TrimEnd();
    }

    /// <summary>
    /// The stable half of the fallback: the first two characters of the session id. It is
    /// the same prefix every CLI verb already accepts, so the name doubles as the handle —
    /// and unlike the CLI's own suffix it is the same two characters after every restart.
    /// </summary>
    private static string Suffix(string sessionId)
    {
        var id = sessionId.TrimStart('{');
        return id.Length >= 2 ? id[..2].ToLowerInvariant() : "";
    }

    private static string FolderOf(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        var trimmed = cwd.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    /// <summary>Lowercase words joined by hyphens, matching the shape the CLI derives.</summary>
    private static string Slug(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            else if (chars.Count > 0 && chars[^1] != '-') chars.Add('-');
        }
        while (chars.Count > 0 && chars[^1] == '-') chars.RemoveAt(chars.Count - 1);
        return new string(chars.ToArray());
    }
}
