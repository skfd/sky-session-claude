namespace SessionCore;

/// <summary>
/// The <c>claude</c> command line this app launches a session with — new, resumed or forked.
///
/// Every one of them carries <c>--remote-control</c>. Remote Control is opt-in per session
/// and can only be asked for at launch or from inside the session, so a window opened
/// without it is reachable only from the machine it sits on: it shows up on the phone and
/// nothing can be typed at it. The sessions this app starts are precisely the ones that
/// want reaching from elsewhere — it opened them in a terminal nobody was watching.
///
/// The name goes in under <c>--name</c>, never as the optional argument to
/// <c>--remote-control</c>: the two are separate flags inside the CLI and only <c>--name</c>
/// reaches the registry (see <see cref="RestartPolicy.ResumeCommand"/>).
/// </summary>
public static class ClaudeLaunch
{
    /// <summary>A session that does not exist yet, started in whatever folder the shell is in.</summary>
    public static string New(string? name = null) => Line(null, name);

    /// <summary>The command that brings <paramref name="sessionId"/> back.</summary>
    public static string Resume(string sessionId, string? name = null) => Line(sessionId, name);

    /// <summary>
    /// A Remote Control <b>host</b> for the folder the shell is in — not a session, but the
    /// server that makes them. <c>claude rc</c> pre-creates one so the phone has somewhere to
    /// type immediately, then spawns more on demand.
    ///
    /// <paramref name="namePrefix"/> is what every session it creates is named after, and it
    /// is the difference between a usable phone list and an unusable one. Left off, the prefix
    /// defaults to the machine's hostname and the rows read <c>cc-pc-sorted-stallman</c> —
    /// which says nothing about which repo you are about to type into. Given the project, they
    /// read <c>sky-session-claude-wondrous-seal</c>, and the eye finds the repo.
    ///
    /// Note this is the opposite rule to <see cref="Line"/>, where a name must go under
    /// <c>--name</c> and never on <c>--remote-control</c>. Different flag, different command,
    /// and this one is the naming lever that works.
    ///
    /// The pre-creation is already the default; it is written out anyway because a phone row
    /// that exists the moment the host does is the whole point of putting one here, and a
    /// default is a thing that can change.
    /// </summary>
    public static string Host(string? namePrefix = null) =>
        "claude rc"
        + (namePrefix is { Length: > 0 }
            ? $" --remote-control-session-name-prefix {SessionName.Quote(namePrefix)}"
            : "")
        + " --create-session-in-dir";

    private static string Line(string? sessionId, string? name)
    {
        var line = "claude";
        if (sessionId is { Length: > 0 }) line += $" --resume {sessionId}";
        if (name is { Length: > 0 }) line += $" --name {SessionName.Quote(name)}";

        // Last, and with no value of its own: --remote-control takes an optional name, so
        // anything following it would have to be that name. Nothing follows it here, and
        // the one thing ever appended downstream — --fork-session — starts with a dash,
        // which the flag will not swallow.
        return $"{line} --remote-control";
    }
}
