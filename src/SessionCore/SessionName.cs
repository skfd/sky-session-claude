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
        return Floor(sessionId, cwd);
    }

    /// <summary>
    /// A name that says what the session is about and where it ran: <c>Subject — repo</c>.
    ///
    /// The subject comes first because it is what tells two sessions apart; the folder is
    /// the disambiguator, not the headline. Three sessions in one repo differ only in their
    /// subjects, and a subject with no folder attached ("Basemap treatments in Chrome")
    /// still identifies the work — which is why the folder is the part that gets dropped
    /// when the line will not hold both.
    /// </summary>
    public static string Compose(string? subject, string? cwd)
    {
        var repo = RepoOf(cwd);
        if (repo.Length == 0) return SentenceCase(Tidy(subject));

        var tail = Separator + repo;

        // A name composed last week comes back through here on the next restart, so the
        // folder is taken off first and put back at the end. Recognising it only after the
        // budget had already cut it would append a second one to the stump of the first.
        var stripped = Tidy(subject, int.MaxValue);
        if (stripped.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            stripped = stripped[..^tail.Length];

        // A subject squeezed into a handful of characters says less than the subject alone,
        // so past that point the folder is the part worth losing.
        int budget = MaxLength - tail.Length;
        if (budget < MinSubject) return SentenceCase(Tidy(stripped));

        var head = SentenceCase(TrimSeparator(Tidy(stripped, budget)));
        return head.Length == 0 ? "" : head + tail;
    }

    /// <summary>
    /// Drop a dangling separator left by the cut, so a truncated subject does not read as
    /// though its folder went missing.
    /// </summary>
    private static string TrimSeparator(string text)
    {
        var t = text.TrimEnd();
        while (t.Length > 0 && (t[^1] == '—' || t[^1] == '-'))
            t = t[..^1].TrimEnd();
        return t;
    }

    /// <summary>
    /// What a session is called when there is nothing to say about it yet: the repo it sits
    /// in and the id prefix that identifies it everywhere else.
    ///
    /// Unlike the CLI's own <c>folder-XX</c>, the two trailing characters are the session
    /// id's — the same after every restart — so the one part of the name that carries no
    /// meaning is at least the part that stays put.
    /// </summary>
    public static string Floor(string sessionId, string? cwd)
    {
        var repo = Slug(RepoOf(cwd));
        var suffix = Suffix(sessionId);

        if (repo.Length == 0) return suffix.Length > 0 ? $"session-{suffix}" : "session";
        return suffix.Length > 0 ? $"{repo}-{suffix}" : repo;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a placeholder rather than a description — our
    /// floor, or the <c>folder-XX</c> the CLI derives.
    ///
    /// This exists because naming writes into the transcript: a floor passed under
    /// <c>--name</c> comes back as a <c>custom-title</c>, and read as a title it would be
    /// composed into a name, written again, and read again — a placeholder made permanent
    /// by being used once.
    ///
    /// It is a shape check, so it is the *fallback*: provenance (<see cref="NameStore"/>) is
    /// how Sky recognises its own names, and this is only for history written before that
    /// store existed. It can misfire on a name genuinely typed that happens to look like
    /// <c>repo-XX</c>, which is why nothing but the pre-store case should lean on it.
    /// </summary>
    public static bool IsFloor(string? name, string sessionId, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var text = name.Trim();
        if (string.Equals(text, Floor(sessionId, cwd), StringComparison.OrdinalIgnoreCase)) return true;

        var repo = Slug(RepoOf(cwd));
        var stem = repo.Length > 0 ? repo : "session";

        if (string.Equals(text, stem, StringComparison.OrdinalIgnoreCase)) return true;

        // The CLI's collision suffix is two characters, drawn fresh on each launch.
        return text.Length == stem.Length + 3
            && text.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && text[stem.Length] == '-'
            && char.IsLetterOrDigit(text[^1])
            && char.IsLetterOrDigit(text[^2]);
    }

    /// <summary>
    /// The repo a session belongs to, which is not always the folder it ran in: three
    /// sessions under <c>&lt;repo&gt;\.claude\worktrees\&lt;branch&gt;</c> are working on one
    /// repo, and naming them after their branches would hide that.
    ///
    /// Anywhere else the leaf folder is taken at face value. Walking up to a real git root
    /// would also mean a session sitting in <c>src\</c> is named after the repo rather than
    /// after where it was standing — a change to what a name means, not a fix, so it is not
    /// made here.
    /// </summary>
    public static string RepoOf(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";

        // The scanner fills an absent cwd with a sentence saying so, rather than leaving it
        // empty, so every caller holding a SessionInfo has a "path" that is really an apology.
        // Slugged it made a folder, and a session called
        // "unknown-cwd-not-found-in-session-file-b9". Refused here as well as at the callers,
        // so the next one to reach for SessionInfo.Cwd cannot repeat it.
        if (cwd == SessionInfo.UnknownCwd) return "";

        var parts = cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals(".claude", StringComparison.OrdinalIgnoreCase)
                && parts[i + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase))
                return parts[i - 1];
        }

        return parts.Length > 0 ? parts[^1] : "";
    }

    /// <summary>
    /// House style: sentence case, in the words they were written in.
    ///
    /// Deliberately only the first letter. Lowercasing the rest would read as tidying and
    /// would quietly eat "Chrome", "OSM" and "Guelph"; the models write sentence case
    /// already, so there is nothing else here to fix.
    /// </summary>
    public static string SentenceCase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        return char.IsLower(t[0]) ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    }

    /// <summary>
    /// What separates the subject from the folder. An em dash rather than a hyphen: the two
    /// are different things, and the folder names are themselves full of hyphens.
    /// </summary>
    private const string Separator = " — ";

    /// <summary>Below this many characters a subject says less than no subject at all.</summary>
    private const int MinSubject = 20;

    /// <summary>
    /// The title a session has actually earned, out of the two its file can carry.
    ///
    /// <paramref name="custom"/> normally wins — it is what a rename or a <c>--name</c> launch
    /// wrote, and that is more recent than the model's first impression. But naming writes
    /// into the transcript, so a placeholder Sky passed under <c>--name</c> is sitting in that
    /// same field, and taken at face value it would be composed into a name, written again,
    /// and read again: a placeholder made permanent by having been used once. So a custom
    /// title shaped like a placeholder is refused here, at the one point where a title is
    /// resolved, which is what stops it from reaching the display, the launch paths and the
    /// policy alike.
    ///
    /// Provenance (<see cref="NameStore"/>) is the reliable test and the policy uses it. This
    /// is the pure one, for the two callers that have no store to hand and for history written
    /// before there was one — and it can misfire on a title genuinely typed that happens to
    /// read like <c>repo-XX</c>, which then shows as untitled beside its project.
    /// </summary>
    public static string? RealTitle(string? custom, string? ai, string sessionId, string? cwd)
    {
        if (custom is { Length: > 0 } && !IsFloor(custom, sessionId, cwd)) return custom;
        if (ai is { Length: > 0 } && !IsFloor(ai, sessionId, cwd)) return ai;
        return null;
    }

    /// <summary>Single-quoted for the PowerShell line the name is typed into.</summary>
    public static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// A title as a name: one line, no runaway length. Kept in the words the model chose
    /// rather than slugged — this is read by a person, and "Art persona website design"
    /// beats "art-persona-website-design" on the one surface that shows it.
    /// </summary>
    /// <param name="max">
    /// The budget, which is <see cref="MaxLength"/> for a name standing on its own and less
    /// for a subject that has a folder to fit alongside it (see <see cref="Compose"/>).
    /// </param>
    public static string Tidy(string? title, int max = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(title) || max <= 0) return "";

        var text = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= max) return text;

        // Cut on a word boundary; an ellipsis in a name is noise, so there is none.
        int cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd();
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
