namespace SessionCore;

/// <summary>
/// The shell line that opens a session in a folder: into the folder first, because the
/// terminal starts wherever it likes, then Claude.
///
/// Separate from <see cref="ClaudeLaunch"/>, which composes the <c>claude</c> command alone
/// and knows nothing about shells. This is the pair of them, and it lives here rather than
/// in either front end because both of them open terminals — <c>SessionCli new</c> and the
/// app when a <c>skysession://new</c> link is clicked — and a folder that is quoted in one
/// place and not the other is a bug nobody finds until a path has an apostrophe in it.
/// </summary>
public static class LaunchLine
{
    public static string NewIn(string folder, string? name = null) =>
        $"cd {SessionName.Quote(folder)}; {ClaudeLaunch.New(name)}";

    /// <summary>
    /// A Remote Control host for a folder — what <c>standby</c> leaves running. The sessions
    /// it creates are named after <paramref name="namePrefix"/>; see
    /// <see cref="ClaudeLaunch.Host"/> for why that is not optional in practice.
    /// </summary>
    public static string HostIn(string folder, string? namePrefix = null) =>
        $"cd {SessionName.Quote(folder)}; {ClaudeLaunch.Host(namePrefix)}";

    /// <summary>
    /// The line that puts a host back up after a restart: the flags it was already running
    /// with, verbatim, in the folder it was serving.
    ///
    /// Verbatim rather than rebuilt, because not every host is standby's. A bare
    /// <c>claude rc</c> typed by hand is a host too, and coming back with standby's session
    /// prefix and spawn mode bolted on would hand back a different host from the one the
    /// restart took away. Only when the command line cannot be read — a process that exited
    /// mid-inspection, or one not ours to inspect — does this fall back to what standby
    /// would have opened, which is the best guess available and worth saying out loud.
    ///
    /// One flag is the exception to verbatim. Standby used to spell out
    /// <c>--create-session-in-dir</c>, and every host it opened before that stopped still
    /// carries it. Putting it back would put back the empty pre-created session with it — the
    /// very thing a restart sweep is the user's only way to retire — so it comes back as the
    /// <c>--no-</c> form. This is safe to treat as standby's signature: the flag is the CLI's
    /// default, and a host typed by hand does not spell its defaults out.
    /// </summary>
    public static string HostAgain(string folder, string? commandLine) =>
        ProcessCommandLine.ArgumentsOf(commandLine) is { } arguments && RemoteControlHosts.IsHostCommand(arguments)
            ? $"cd {SessionName.Quote(folder)}; claude {WithoutPreCreation(arguments)}"
            : HostIn(folder, Standby.ProjectOf(folder));

    /// <summary>The arguments with the pre-creation flag turned off, wherever it sat.</summary>
    private static string WithoutPreCreation(string arguments) =>
        System.Text.RegularExpressions.Regex.Replace(
            arguments, @"(?<![\w-])--create-session-in-dir(?![\w-])", "--no-create-session-in-dir");
}
