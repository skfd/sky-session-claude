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
