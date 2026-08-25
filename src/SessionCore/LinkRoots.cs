using System.Text.Json;

namespace SessionCore;

/// <summary>
/// The folders a <c>skysession://new</c> link may open a session in.
///
/// The alternative was the rule the brief's inbox uses — allow any folder that already has
/// sessions in it, a list nobody has to maintain because it is the places you have already
/// worked. It is a good rule for a queue and the wrong one for a link: the strongest thing
/// <c>new</c> is for is a repo you just cloned, which by definition has no sessions yet.
///
/// So it is configuration, and configuration on this machine should not mean a constant
/// compiled into an exe. <c>%APPDATA%\sky-session-claude\settings.json</c> sits beside the
/// marks and the names:
///
/// <code>
/// { "linkRoots": [ "C:\\Users\\kk\\Code", "D:\\Work" ] }
/// </code>
///
/// Nothing in this app writes that file — it is edited by hand or not at all — so it gets a
/// plain read rather than <see cref="JsonSidecar{TValue}"/>, whose atomic replace and
/// merge-on-write exist for files with two writers. What it does keep is the sidecar's one
/// rule that matters to a reader: a file that is there but unreadable never silently becomes
/// the default. Falling back would quietly widen the allowlist to <c>~/Code</c> at the exact
/// moment someone had narrowed it, so a broken file allows nothing and says why.
/// </summary>
public sealed class LinkRoots
{
    /// <summary>Where a link may open a session. Empty means no link may open one at all.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Set when the file was there and could not be used. Worth showing.</summary>
    public string? Warning { get; }

    private LinkRoots(IReadOnlyList<string> roots, string? warning)
    {
        Roots = roots;
        Warning = warning;
    }

    public static string DefaultPath() =>
        Path.Combine(DispositionStore.DefaultDir(), "settings.json");

    /// <summary>
    /// The folder assumed when there is no settings file: every repo on this machine lives
    /// under <c>~/Code</c>. A default is not a policy — the moment the file exists, it is
    /// the only answer.
    /// </summary>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code");

    public static LinkRoots Load(string? path = null)
    {
        path ??= DefaultPath();

        if (!File.Exists(path))
            return new LinkRoots([DefaultRoot()], null);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LinkRoots([], $"{path} could not be read ({e.Message}), so no link may start a session");
        }

        Settings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(text, Options);
        }
        catch (JsonException e)
        {
            return new LinkRoots([], $"{path} is not valid JSON ({e.Message}), so no link may start a session");
        }

        // A file with no linkRoots key at all is not a broken file — it is a settings file
        // that has other things in it. That means the default, same as no file.
        if (settings?.LinkRoots is null)
            return new LinkRoots([DefaultRoot()], null);

        var roots = settings.LinkRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Expand)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        // An explicit empty list is a decision — someone turned `new` off — and is left as
        // it is. A list whose every entry was unusable is not, so it says so.
        if (roots.Count == 0 && settings.LinkRoots.Count > 0)
            return new LinkRoots([], $"No usable folder in linkRoots in {path}, so no link may start a session");

        return new LinkRoots(roots, null);
    }

    /// <summary>
    /// How a link should name <paramref name="folder"/>: relative to whichever root contains
    /// it, or null when no root does.
    ///
    /// This is the producer's half of the rule <see cref="SessionUri"/> enforces on the way
    /// back in. Whoever is writing a link has an absolute path in hand — it is what they are
    /// looking at — and the link must not carry one.
    /// </summary>
    public string? Relative(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var root in Roots)
        {
            if (!SessionUri.Under(full, root)) continue;

            var trimmed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var relative = full[trimmed.Length..].Trim(Path.DirectorySeparatorChar);

            // The root itself has nothing left over, and a link needs a folder to name.
            if (relative.Length > 0) return relative;
        }

        return null;
    }

    /// <summary>
    /// <c>~</c> is what someone writing this by hand will type, and it is not a folder any
    /// Windows API knows. Anything that will not resolve is dropped rather than thrown: one
    /// bad line should cost its own entry, not the whole file.
    /// </summary>
    private static string? Expand(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith('~'))
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                 + text[1..].Replace('/', Path.DirectorySeparatorChar);

        try
        {
            var full = Path.GetFullPath(text);
            return Path.IsPathFullyQualified(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Settings
    {
        public List<string>? LinkRoots { get; set; }
    }
}
