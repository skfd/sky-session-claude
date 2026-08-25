namespace SessionCore;

/// <summary>What a <c>skysession://</c> link asks for.</summary>
public enum SessionUriVerb
{
    /// <summary>Reopen a session in its terminal.</summary>
    Resume,

    /// <summary>Tick a session off. Opens nothing.</summary>
    Done,

    /// <summary>Start a session in a folder.</summary>
    New,
}

/// <summary>
/// A parsed link, or the reason it was refused. One of <see cref="Verb"/> and
/// <see cref="Refusal"/> is always set and never both.
/// </summary>
public sealed class SessionUriRequest
{
    public SessionUriVerb? Verb { get; init; }

    /// <summary>The session id, for <see cref="SessionUriVerb.Resume"/> and
    /// <see cref="SessionUriVerb.Done"/>. May be a prefix, resolved by the caller.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// The folder, for <see cref="SessionUriVerb.New"/>. The link named it relative to a
    /// configured root; this is what that resolved to, absolute and known to exist.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>Why this link was not accepted, in words worth showing someone.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    internal static SessionUriRequest No(string why) => new() { Refusal = why };
}

/// <summary>
/// Parses <c>skysession://</c> links, and refuses everything else.
///
/// This is the whole security boundary of the link feature, which is why it is a pure
/// function over a string with no windows, no registry and no side effects: the hostile
/// inputs can be written as ordinary tests.
///
/// The bug this is built against is one bug, seen many times — <c>ms-msdt</c> (Follina),
/// <c>steam://</c>, <c>zoommtg://</c>, a long line of Electron argument-injection holes. A
/// page navigates to a scheme, Windows appends the attacker's string to the registered
/// <c>shell\open\command</c>, and it executes. Nothing here builds a command line from URL
/// text: parsing produces a typed request, and the caller reaches the same entry points a
/// person typing at the CLI reaches.
///
/// The payload for <c>resume</c> and <c>done</c> is a session id, so it is matched against
/// what an id may contain rather than against a list of characters someone thought to
/// forbid. An allowlist costs nothing here and is the difference between refusing the
/// attacks that were imagined and accepting only what is valid.
/// </summary>
public static class SessionUri
{
    public const string Scheme = "skysession";

    /// <summary>
    /// The shortest id a link may carry. `SessionCli` resolves any unique prefix, but a link
    /// is clicked by someone who cannot see what it matched, so two characters that happen
    /// to be unique today and ambiguous next week is not a bargain worth taking.
    /// </summary>
    public const int MinIdLength = 8;

    /// <summary>
    /// Parse a link. <paramref name="roots"/> are the folders <c>new</c> may open a session
    /// in; pass an empty list to refuse <c>new</c> outright.
    /// </summary>
    public static SessionUriRequest Parse(string? url, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(url))
            return SessionUriRequest.No("Empty link.");

        // Rule 2: re-validate rather than trust what the shell handed over. A registered
        // handler is invoked with whatever the page put in the address bar.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return SessionUriRequest.No($"Not a link: {Trim(url)}");

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return SessionUriRequest.No($"Not a {Scheme}:// link: {Trim(url)}");

        // skysession://resume/<id> puts the verb in the authority and the id in the path.
        var verb = uri.Host.ToLowerInvariant();
        return verb switch
        {
            "resume" => WithId(SessionUriVerb.Resume, uri),
            "done" => WithId(SessionUriVerb.Done, uri),
            "new" => WithFolder(uri, roots),

            // Named on purpose: these are the verbs a bad link would want, and saying so is
            // more useful than "unknown", both to whoever clicked and to whoever reads a log.
            "fork" or "restart" or "trust" or "close" => SessionUriRequest.No(
                $"'{verb}' is deliberately not something a link can do."),

            "" => SessionUriRequest.No($"No verb in {Trim(url)}."),
            _ => SessionUriRequest.No($"Unknown verb '{verb}'."),
        };
    }

    private static SessionUriRequest WithId(SessionUriVerb verb, Uri uri)
    {
        var id = uri.AbsolutePath.Trim('/');
        if (id.Length == 0)
            return SessionUriRequest.No($"'{uri.Host}' needs a session id.");

        // The allowlist. A session id is a GUID, and a link may carry a prefix of one, so
        // hex and dashes is the entire alphabet. Anything else — a quote, a space, a
        // separator, a percent-escape that survived unescaping — is not a truncated id, it
        // is something else wearing one's clothes.
        if (!id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' or '-'))
            return SessionUriRequest.No($"Not a session id: {Trim(id)}");

        if (id.Length < MinIdLength)
            return SessionUriRequest.No(
                $"Session id '{id}' is too short for a link — {MinIdLength} characters at least.");

        if (id.Length > 36)
            return SessionUriRequest.No($"Not a session id: {Trim(id)}");

        return new SessionUriRequest { Verb = verb, Id = id.ToLowerInvariant() };
    }

    private static SessionUriRequest WithFolder(Uri uri, IReadOnlyList<string> roots)
    {
        var query = Query(uri);
        if (!query.TryGetValue("in", out var raw) || string.IsNullOrWhiteSpace(raw))
            return SessionUriRequest.No("'new' needs a folder: skysession://new?in=<folder>.");

        // Rule 5, stated by absence: a link may say where, never what to say once it is
        // there. A link that opens a session and also sends it a prompt is remote code
        // execution with an extra step. Refused rather than ignored, so a link written
        // expecting it to work fails loudly instead of quietly doing half of what it said.
        if (query.ContainsKey("prompt"))
            return SessionUriRequest.No("A link cannot carry a prompt.");

        // The folder is named relative to a configured root, never absolutely. That is worth
        // more than tidiness: a link that cannot say where the filesystem starts cannot name
        // a UNC share, a device path, or another drive, so the whole class of "escape the
        // allowlist by spelling the path differently" is gone by construction rather than by
        // a list of prefixes someone remembered to forbid. It also makes a link portable —
        // `?in=address-vault` means the same thing on a machine that keeps its repos
        // somewhere else.
        var text = raw.Trim().Replace('/', Path.DirectorySeparatorChar);

        if (text.Contains('\0') || text.Contains('\n') || text.Contains('\r'))
            return SessionUriRequest.No("Not a folder.");

        // Rooted in any of Windows' several senses: C:\x, \x, C:x, \\server\share.
        if (Path.IsPathRooted(text) || text.Contains(':'))
            return SessionUriRequest.No(
                $"A link names a folder relative to a configured root, not an absolute path: {Trim(raw)}");

        var segments = text.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Refused before resolution rather than caught after it. Both checks are kept — this
        // one so the refusal can say what was wrong, and the containment check below because
        // a link should not be one clever normalisation away from reaching the whole disk.
        if (segments.Any(s => s == ".."))
            return SessionUriRequest.No($"A link cannot climb out of its root: {Trim(raw)}");

        segments = segments.Where(s => s != ".").ToArray();
        if (segments.Length == 0)
            return SessionUriRequest.No("'new' needs a folder: skysession://new?in=<folder>.");

        if (roots.Count == 0)
            return SessionUriRequest.No("No folders are configured for links to open sessions in.");

        var relative = string.Join(Path.DirectorySeparatorChar, segments);
        var found = new List<string>();

        foreach (var root in roots)
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(root, relative)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (Under(candidate, root) && Directory.Exists(candidate)) found.Add(candidate);
        }

        // Ambiguity is an error rather than a guess, the same way a session id prefix is: the
        // person who clicked cannot see which root answered, and picking the first would make
        // the link mean something different the day a second root is configured.
        if (found.Count > 1)
            return SessionUriRequest.No(
                $"'{relative}' exists under more than one root ({string.Join(", ", found)}), so the link is ambiguous.");

        if (found.Count == 0)
            return SessionUriRequest.No($"No folder '{relative}' under any root links may open sessions in.");

        return new SessionUriRequest { Verb = SessionUriVerb.New, Folder = found[0] };
    }

    /// <summary>
    /// The query string as a map, unescaped, last value winning.
    ///
    /// Hand-rolled rather than reached for: the framework's query parser lives in an
    /// assembly this library does not otherwise need, and the grammar here is one <c>?</c>,
    /// a few <c>&amp;</c>, and one <c>=</c> per pair. A duplicate key takes the last value
    /// so that <c>?in=a&amp;in=b</c> cannot mean one thing to the check and another to the
    /// launch — the classic way an allowlist gets walked past.
    /// </summary>
    private static Dictionary<string, string> Query(Uri uri)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = uri.Query.TrimStart('?');
        if (text.Length == 0) return map;

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(split[0]);
            if (key.Length == 0) continue;
            map[key] = split.Length == 2 ? Uri.UnescapeDataString(split[1]) : "";
        }

        return map;
    }

    /// <summary>
    /// Is <paramref name="folder"/> inside <paramref name="root"/>?
    ///
    /// Both ends get a trailing separator before they are compared, which is the whole
    /// reason this is a function: <c>C:\CodeEvil</c> starts with <c>C:\Code</c> as a string
    /// and is nowhere near it as a folder. The root itself counts as inside itself.
    /// </summary>
    internal static bool Under(string folder, string root)
    {
        string full;
        try { full = Path.GetFullPath(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var a = WithSeparator(folder);
        var b = WithSeparator(full);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>
    /// A refusal quotes what it refused, and a link is as long as whoever wrote it liked.
    /// Control characters go too: this text ends up in a dialog and in a log line.
    /// </summary>
    private static string Trim(string text)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return clean.Length <= 60 ? clean : clean[..60] + "...";
    }
}
