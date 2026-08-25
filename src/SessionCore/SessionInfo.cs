namespace SessionCore;

/// <summary>
/// Everything extracted from a single session file. Mirrors the pscustomobject the
/// original Get-SessionInfo returned, plus the file-level fields Get-SessionRows added.
/// </summary>
public sealed class SessionInfo
{
    // --- from the session file body -------------------------------------------
    /// <summary>What the scanner writes for a session that has earned no title.</summary>
    public const string Untitled = "(untitled)";

    /// <summary>
    /// What the scanner writes for a session file with no recorded cwd. <see cref="Cwd"/> is
    /// therefore never empty, which makes it the wrong field to ask whether a folder is known
    /// -- and read as a path this sentence slugs into a folder name, which once had a session
    /// called "unknown-cwd-not-found-in-session-file-b9".
    /// </summary>
    public const string UnknownCwd = "<unknown - cwd not found in session file>";

    /// <summary>The folder the session ran in, or null when the file never recorded one.</summary>
    public string? RealCwd => string.IsNullOrEmpty(Cwd) || Cwd == UnknownCwd ? null : Cwd;

    public string? Cwd { get; init; }
    public string? Name { get; init; }          // custom title wins over AI title

    /// <summary>
    /// The <c>custom-title</c> in the file — what <c>--name</c> or a rename wrote. It is the
    /// only name a session that is not running has, and the only one that can be a placeholder
    /// Sky put there itself.
    /// </summary>
    public string? CustomTitle { get; init; }

    /// <summary>The model-written title, generated once early and never revisited.</summary>
    public string? AiTitle { get; init; }

    /// <summary>
    /// Whether the session has done anything at all. A terminal opened and never used has no
    /// subject to find, so the floor is the honest name for it rather than a failure to find
    /// a better one.
    /// </summary>
    public bool HasContent => !string.IsNullOrEmpty(LastPrompt) || !string.IsNullOrEmpty(Recap);

    /// <summary>
    /// The session's own title, or null when it has none. <see cref="Name"/> is filled in
    /// with <see cref="Untitled"/> so a column always has something to show, which makes it
    /// the wrong field to ask whether a title exists — asking it that way once named a
    /// restarted session "(untitled)".
    /// </summary>
    public string? Title => string.IsNullOrEmpty(Name) || Name == Untitled ? null : Name;
    public string LastPrompt { get; init; } = "";
    public string Recap { get; init; } = "";
    public SessionStatus Status { get; init; }
    public bool Complete => Status == SessionStatus.Complete;
    public int ContextTokens { get; init; }
    public int? ContextPct { get; init; }
    public bool IsLargeContext { get; init; }     // session ran with the 1M window

    // --- from the file on disk ----------------------------------------------
    public string SessionId { get; init; } = "";     // session file base name
    public string FilePath { get; init; } = "";      // full path, for fork-from-point
    public DateTime LastActive { get; init; }

    /// <summary>
    /// When the session file was last written. Opening a session rewrites it, so this
    /// runs ahead of <see cref="LastActive"/> for a session that was reopened and left
    /// alone — which is the whole reason the two are separate fields.
    /// </summary>
    public DateTime LastTouched { get; init; }

    /// <summary>
    /// When the sitting before the current one ended, for the "previous -> latest" age
    /// on a card. Null when the session is one unbroken stretch of work.
    /// </summary>
    public DateTime? PreviousActive { get; init; }

    public double AgeDays { get; init; }
    public double SizeKB { get; init; }

    // --- derived display fields ---------------------------------------------
    public string Project { get; init; } = "";
    public string Command { get; init; } = "";

    /// <summary>
    /// <see cref="Command"/> with the session named, for the paths that open a terminal on
    /// this same session. Left off <see cref="Command"/> itself because forking appends
    /// <c>--fork-session</c> to it, and the fork is a different session that has not earned
    /// this one's name.
    ///
    /// Rebuilt through <see cref="ClaudeLaunch"/> rather than appended to <see cref="Command"/>:
    /// that line now ends with <c>--remote-control</c>, which takes an optional name, and a
    /// <c>--name</c> tacked on after it reads as an argument to the wrong flag. Composing in
    /// one place is what keeps the order a decision rather than an accident.
    /// </summary>
    /// <param name="name">
    /// What to call it, from <see cref="SessionNaming.NameForLaunch"/>. This used to compose a
    /// name here, which meant a second decider that knew nothing about which names were Sky's
    /// own — so every resume rewrote the last placeholder back into the transcript.
    /// </param>
    public string CommandNamed(string name) => Command.Length == 0
        ? ""
        : $"cd \"{Cwd}\"; {ClaudeLaunch.Resume(SessionId, name)}";
    public bool Unfinished { get; init; }
    public string WaitingOn { get; init; } = "";
}
