using System.Diagnostics;

namespace SessionCore;

/// <summary>What happened to one close, in a sentence fit for the status line.</summary>
public readonly record struct CloseResult(bool Ok, string Message)
{
    public static CloseResult Fail(string why) => new(false, why);
    public static CloseResult Done(string what) => new(true, what);
}

/// <summary>
/// Quits a live session, and by default takes its terminal with it — the end-of-day sweep.
///
/// The first half of this is the first half of a restart: ask Claude to quit in a way that
/// can never send a half-typed draft, then wait for the process to actually go.
/// <see cref="SessionRestarter"/> calls the same <see cref="QuitAsync"/>, so there is one
/// copy of that gesture and one set of timeouts behind both verbs.
///
/// Where the two part is what happens next. A restart must have a shell to type the resume
/// into, and refuses without one; a close does not — a Claude started as the tab's own root
/// process takes the tab down when it quits, which for a cleanup is the point. When there
/// <em>is</em> a shell underneath, closing the session alone would leave an empty terminal
/// per session and defeat the whole exercise, so the shell is sent an <c>exit</c> too. Pass
/// <paramref name="keepTerminal"/> to stop at the session and leave the prompt sitting there.
/// </summary>
public static class SessionCloser
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Close <paramref name="live"/>. Safety is the caller's to decide
    /// (see <see cref="ClosePolicy"/>); this is only the mechanism.
    /// </summary>
    public static async Task<CloseResult> CloseAsync(LiveSession live, bool keepTerminal = false)
    {
        // Read the shell before the session goes: the walk up the tree runs through Claude's
        // own process, so once it has exited there is nothing left to walk from.
        var shell = keepTerminal ? null : LiveSessions.ShellFor(live.Pid);

        if (await QuitAsync(live.Pid) is { } why) return CloseResult.Fail(why);

        if (shell is null)
            return CloseResult.Done(keepTerminal ? "closed" : "closed, and its terminal with it");

        await Task.Delay(600);   // let the shell finish repainting its prompt

        return await Task.Run(() => ConsoleInput.SendLine(shell.Value, "exit"))
            ? CloseResult.Done("closed, and its terminal with it")
            : CloseResult.Done("closed, but its terminal is still open");
    }

    /// <summary>
    /// Ask the CLI at <paramref name="pid"/> to quit and wait for it to go. Null when it
    /// went; otherwise the sentence explaining what to do about it.
    ///
    /// Shared with <see cref="SessionRestarter"/>, which needs exactly this and then types a
    /// resume at whatever the terminal came back to.
    /// </summary>
    internal static async Task<string?> QuitAsync(int pid)
    {
        if (!await Task.Run(() => ConsoleInput.SendExitGesture(pid)))
            return "could not reach its terminal";

        return await WaitForExit(pid, ExitTimeout)
            ? null
            : "it did not quit — left alone, nothing was changed";
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
}
