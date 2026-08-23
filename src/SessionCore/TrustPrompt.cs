using System.Diagnostics;

namespace SessionCore;

/// <summary>
/// Claude Code's "do you trust the files in this folder?" dialog, read from outside the
/// session and answered on the operator's behalf.
///
/// It is the one thing standing between <c>new</c> and a usable session in a folder Claude
/// Code has not seen before, and it is invisible to everything else this tool knows how to
/// read: nothing is written to the session file — the session does not have a file yet —
/// so the only witness is the screen.
///
/// Answering is Enter, and Enter is exactly why the screen has to be read first. The dialog
/// is a two-item menu, and the second item is "No, exit": pressing Enter at a moment when
/// the operator (or an earlier keystroke) has moved the selection down does not decline the
/// folder, it closes the terminal. So this refuses unless it can see the yes option and see
/// that it is the selected one. Anything else comes back with the screen and nothing typed.
/// </summary>
public static class TrustPrompt
{
    public enum State
    {
        /// <summary>No trust dialog on screen — whatever is there, it is not ours to answer.</summary>
        NotShowing,

        /// <summary>The dialog, with "Yes, I trust this folder" selected. Enter accepts it.</summary>
        YesSelected,

        /// <summary>The dialog, with something else selected. Enter would take that instead.</summary>
        OtherSelected,
    }

    /// <summary>
    /// The wording of the yes option, which is what identifies the dialog. The surrounding
    /// prose has changed between CLI versions and the box drawing is cosmetic; the option
    /// itself is the stable part, and it is also the only line we need to be sure about.
    /// </summary>
    private const string Yes = "Yes, I trust this folder";

    /// <summary>The marker the CLI paints against the selected item in a menu.</summary>
    private const char Selected = '❯';   // ❯

    public static State Read(string screen)
    {
        if (string.IsNullOrEmpty(screen)) return State.NotShowing;

        foreach (var line in screen.Split('\n'))
        {
            if (line.IndexOf(Yes, StringComparison.OrdinalIgnoreCase) < 0) continue;
            return line.Contains(Selected) ? State.YesSelected : State.OtherSelected;
        }

        return State.NotShowing;
    }

    /// <summary>
    /// True when the dialog on screen is the one for <paramref name="folder"/>. The dialog
    /// names the workspace it is asking about, so this is what stops a launch in one repo
    /// from answering a prompt that happens to be open in another.
    ///
    /// A path too long for the box is wrapped or clipped, so the folder's own name is
    /// accepted as well. On its own that would be thin evidence; here it corroborates a
    /// process we started ourselves, in that folder, seconds ago.
    /// </summary>
    public static bool IsAbout(string screen, string folder)
    {
        var full = folder.TrimEnd('\\', '/');
        if (screen.Contains(full, StringComparison.OrdinalIgnoreCase)) return true;

        var cut = full.LastIndexOfAny(['\\', '/']);
        var leaf = cut >= 0 ? full[(cut + 1)..] : full;
        return leaf.Length > 0 && screen.Contains(leaf, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Take the selected option. Only ever called once <see cref="Read"/> says yes is it.</summary>
    public static bool Accept(int pid) => ConsoleInput.SendLine(pid, "");

    /// <summary>
    /// The process sitting at a trust prompt for <paramref name="folder"/>, or null if none
    /// turns up before <paramref name="timeout"/>.
    ///
    /// A session waiting on this dialog is not in the live registry — it registers after the
    /// folder is trusted, which is the whole problem — so it cannot be found the way every
    /// other verb finds a session. What it is instead is a claude process younger than the
    /// launch we just made, whose screen shows this dialog naming this folder. All three
    /// have to hold before anything is typed.
    /// </summary>
    public static int? FindWaiting(string folder, DateTime launchedAfter, TimeSpan timeout, TimeSpan poll)
    {
        var deadline = DateTime.Now + timeout;

        while (true)
        {
            foreach (var pid in Candidates(launchedAfter))
            {
                var screen = ConsoleInput.ReadScreen(pid);
                if (Read(screen) == State.YesSelected && IsAbout(screen, folder)) return pid;
            }

            if (DateTime.Now >= deadline) return null;
            Thread.Sleep(poll);
        }
    }

    /// <summary>
    /// Claude processes started since the launch. The name is matched by prefix because an
    /// update renames the binary out from under running sessions (claude.exe.old.&lt;stamp&gt;);
    /// those are older than any launch of ours, but the prefix costs nothing and keeps this
    /// consistent with how <see cref="LiveSessions"/> matches.
    /// </summary>
    private static IEnumerable<int> Candidates(DateTime launchedAfter)
    {
        foreach (var p in Process.GetProcesses())
        {
            int pid;
            try
            {
                if (!p.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.StartTime < launchedAfter) continue;
                pid = p.Id;
            }
            catch { continue; }        // exited, or a process we may not ask about
            finally { p.Dispose(); }

            yield return pid;
        }
    }
}
