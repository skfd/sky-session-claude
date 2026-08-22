using System.Diagnostics;

namespace SessionCore;

/// <summary>What happened to one restart, in a sentence fit for the status line.</summary>
public readonly record struct RestartResult(bool Ok, string Message)
{
    public static RestartResult Fail(string why) => new(false, why);
    public static RestartResult Done(string what) => new(true, what);
}

/// <summary>
/// Restarts a live session in the terminal it is already sitting in.
///
/// Claude Code updates in place, so a session keeps the build it started with until it is
/// restarted — and after an update every terminal starts asking at once. The alternative
/// to this is closing a dozen tabs by hand and resuming a dozen sessions by hand.
///
/// The sequence is: ask Claude to quit (never in a way that could send a half-typed
/// draft), wait for it to actually go, then type the resume command at the shell it hands
/// the terminal back to. Nothing is killed and no window is raised; if any step does not
/// land, the session is left exactly as it was and the operator is told what to type.
/// </summary>
public static class SessionRestarter
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Restart <paramref name="live"/> in place. Safety is the caller's to decide
    /// (see <see cref="RestartPolicy"/>); this is only the mechanism.
    /// </summary>
    public static async Task<RestartResult> RestartAsync(LiveSession live)
    {
        // Find the shell first: with nothing to hand the terminal back to, quitting Claude
        // would close the tab and strand the session, which is worse than leaving it stale.
        if (LiveSessions.ShellFor(live.Pid) is not { } shell)
            return RestartResult.Fail(
                "its terminal has no PowerShell to come back to — restart this one by hand");

        if (!await Task.Run(() => ConsoleInput.SendExitGesture(live.Pid)))
            return RestartResult.Fail("could not reach its terminal");

        if (!await WaitForExit(live.Pid, ExitTimeout))
            return RestartResult.Fail("it did not quit — left alone, nothing was changed");

        await Task.Delay(600);   // let the shell finish repainting its prompt

        var line = RestartPolicy.RelaunchLine(live);
        if (!await Task.Run(() => ConsoleInput.SendLine(shell, line)))
            return RestartResult.Fail($"it quit, but the resume did not go in — type: {line}");

        // Only the registry can confirm the session is genuinely back up, on which build,
        // and whether Remote Control reconnected. Saying so before it does would be a guess.
        var back = await WaitForReturn(live, ReturnTimeout);
        if (back is null)
            return RestartResult.Fail($"it quit and was resumed, but has not reported back yet — check its terminal");

        var note = $"back on {back.Version ?? "an unknown build"}";
        if (live.RemoteControl)
            note += back.RemoteControl ? ", Remote Control reconnected" : ", but Remote Control did not reconnect";

        return RestartResult.Done(note);
    }

    private static async Task<bool> WaitForExit(int pid, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
            }
            catch { return true; }   // gone from the table entirely
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>
    /// The same session id published under a different pid — the restarted CLI announcing
    /// itself. Matching on the id alone would keep seeing the corpse's registry file, which
    /// outlives the process it describes.
    /// </summary>
    private static async Task<LiveSession?> WaitForReturn(LiveSession old, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);

            var running = await Task.Run(() =>
                LiveSessions.Scan().TryGetValue(old.SessionId, out var found) ? found : null);

            if (running?.FirstOrDefault(r => r.Pid != old.Pid) is { } back)
            {
                // Remote Control connects a moment after the session does; give it that moment.
                if (old.RemoteControl && !back.RemoteControl && DateTime.UtcNow < until) continue;
                return back;
            }
        }
        return null;
    }
}
