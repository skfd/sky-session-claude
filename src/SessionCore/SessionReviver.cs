using System.Diagnostics;

namespace SessionCore;

/// <summary>One process found holding a session, and whether it ever announced itself.</summary>
/// <param name="Pid">The claude process.</param>
/// <param name="Registered">
/// True when it published a registry entry. False means it is running and resuming this
/// session but never registered — the state a session hangs in when it starts and stalls.
/// </param>
/// <param name="Shell">The PowerShell its terminal falls back to, or null if nothing survives it.</param>
public readonly record struct SessionHolder(int Pid, bool Registered, int? Shell);

/// <summary>What a force-resume did.</summary>
public readonly record struct ReviveResult(bool Ok, string Message, IReadOnlyList<int> Cleared)
{
    public static ReviveResult Fail(string why) => new(false, why, Array.Empty<int>());
}

/// <summary>
/// Resumes a session even though something is already holding it.
///
/// The ordinary <c>resume</c> refuses when a session is already open, and it is right to:
/// two <c>--resume</c>s of one conversation are two processes writing one file. But the
/// check reads the registry, and a session that starts and hangs before registering is not
/// in it — so the verbs report "not open in a terminal" about a process that is very much
/// running, and the session is stranded with no way back through the tool that stranded it.
///
/// This is the way out. It finds the holder by command line as well as by registry, ends
/// it, and resumes in the terminal it vacated. Unlike <see cref="SessionRestarter"/> it
/// does not ask nicely first: a process that is hung is exactly one that will not answer a
/// Ctrl+C, and waiting twenty seconds to learn that helps nobody. The conversation is on
/// disk and <c>--resume</c> replays it, so what a kill costs is what lives only in the
/// process — a turn in flight, an unsent draft. That is the operator's call to make, which
/// is why nothing here happens without <c>--force</c>.
/// </summary>
public static class SessionReviver
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every process holding <paramref name="sessionId"/>: the registered one if there is
    /// one, plus any that is resuming the session without having registered.
    ///
    /// The two sources agree in the normal case and the registry is the cheaper of them, but
    /// it is the command line that answers the case this verb exists for.
    /// </summary>
    public static List<SessionHolder> Holders(string sessionId)
    {
        var registered = new HashSet<int>();
        if (LiveSessions.Scan().TryGetValue(sessionId, out var live))
            foreach (var session in live) registered.Add(session.Pid);

        var holders = new List<SessionHolder>();
        var seen = new HashSet<int>();

        foreach (var pid in ProcessCommandLine.ResumingPids(sessionId).Concat(registered))
        {
            if (!seen.Add(pid)) continue;
            holders.Add(new SessionHolder(pid, registered.Contains(pid), LiveSessions.ShellFor(pid)));
        }

        return holders;
    }

    /// <summary>
    /// End everything holding the session and resume it — in the terminal the holder
    /// vacated where one survives it, in a new terminal otherwise.
    ///
    /// <paramref name="command"/> is the full relaunch line (<see cref="SessionInfo.NamedCommand"/>),
    /// which already carries the <c>cd</c> back to the session's folder.
    /// </summary>
    public static ReviveResult Revive(string sessionId, string command, IReadOnlyList<SessionHolder> holders)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ReviveResult.Fail("it has no resumable command (no recorded cwd)");

        var cleared = new List<int>();
        foreach (var holder in holders)
        {
            if (!Kill(holder.Pid)) return ReviveResult.Fail($"could not end pid {holder.Pid}; nothing was changed");
            cleared.Add(holder.Pid);
        }

        // Reuse a freed terminal rather than leaving an empty one behind and opening
        // another. Only a shell that outlived its session can take the command, and only
        // the first — the others are someone else's tabs to close.
        var shell = holders.Select(h => h.Shell).FirstOrDefault(s => s is not null);
        if (shell is { } target)
        {
            Thread.Sleep(600);   // let the shell finish repainting its prompt
            if (ConsoleInput.SendLine(target, command))
                return new ReviveResult(true, Note(cleared, $"resumed in the terminal it was already in (shell {target})"), cleared);
        }

        TerminalLauncher.Start(command);
        return new ReviveResult(true, Note(cleared, "resumed in a new terminal"), cleared);
    }

    private static string Note(IReadOnlyList<int> cleared, string what) =>
        cleared.Count == 0
            ? char.ToUpperInvariant(what[0]) + what[1..] + "."
            : $"Ended {Pids(cleared)} and {what}.";

    private static string Pids(IReadOnlyList<int> cleared) =>
        cleared.Count == 1 ? $"pid {cleared[0]}" : "pids " + string.Join(", ", cleared);

    /// <summary>
    /// Kill and wait for the process to actually leave the table. Typing the resume at a
    /// shell whose Claude is still exiting puts the line into Claude's input box instead.
    /// </summary>
    private static bool Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return process.WaitForExit((int)ExitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException) { return true; }   // already gone from the table
        catch { return false; }
    }
}
