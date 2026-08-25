namespace SessionCore;

/// <summary>
/// What opening a session again means right now: a command to type, a terminal that is
/// already showing it, or a reason it cannot be done.
/// </summary>
/// <param name="Command">The line to run in a new terminal. Null only when there is nothing to run.</param>
/// <param name="AlreadyLive">
/// The session, when the registry already has it open somewhere. Reported rather than acted
/// on: raising that terminal is right for a click, and wrong for <c>resume --force</c>, which
/// exists to end a holder and start again. So the command comes back either way and the
/// caller decides.
/// </param>
/// <param name="Refusal">Why not, when there is no command to give.</param>
public sealed record ResumePlan(string? Command, LiveSession? AlreadyLive, string? Refusal);

/// <summary>
/// Reopening a session, decided in one place.
///
/// Both front ends do this — <c>SessionCli resume</c>, and the app when a
/// <c>skysession://resume</c> link is clicked — and the parts worth getting wrong are the
/// same for both: a session that is already up must be raised rather than started twice,
/// and the name on the command line must come from the naming policy rather than from
/// whoever is composing the launch. A second decider here is what used to write Sky's own
/// last placeholder back into the transcript on every resume.
/// </summary>
public static class SessionResume
{
    /// <param name="dry">
    /// Plan the name without claiming it. A dry run promises to change nothing, and
    /// <c>names.json</c> is something.
    /// </param>
    public static ResumePlan Plan(SessionInfo info, NameStore names, bool dry = false)
    {
        if (string.IsNullOrEmpty(info.Command))
            return new ResumePlan(null, null,
                $"{info.SessionId} has no resumable command (no recorded cwd).");

        var inputs = SessionNaming.InputsFor(info, live: null, SessionNaming.LiveNamesOf(LiveSessions.Scan()));
        var name = dry
            ? SessionNaming.PlanLaunch(inputs, names).Name!
            : SessionNaming.NameForLaunch(inputs, names);

        return new ResumePlan(info.CommandNamed(name), LiveSessions.Find(info.SessionId), null);
    }
}
