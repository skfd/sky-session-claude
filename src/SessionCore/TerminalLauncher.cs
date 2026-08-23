using System.Diagnostics;

namespace SessionCore;

/// <summary>
/// Open a terminal and run a command in it — the one way this app starts a session, whether
/// the ask came from a double-click, a CLI verb, or an agent.
///
/// Two things have to be true of the terminal, and each one dictates part of how it starts.
///
/// It needs a console of its own, so the command goes through <c>cmd /c start</c> rather
/// than straight to PowerShell. A child started directly inherits its parent's console:
/// harmless from the window, which is a GUI process and gets a fresh one, and fatal from the
/// CLI, where the console it inherits is whatever the caller had. Run from an agent's shell,
/// stdin is redirected, and Claude Code reads that as <c>--print</c> mode and exits with
/// "Input must be provided either through stdin or as a prompt argument" before the operator
/// sees anything. <c>start</c> always makes a new console.
///
/// And it must inherit nothing of ours, which is why this launches through the shell
/// (<see cref="ProcessStartInfo.UseShellExecute"/>) rather than editing the child's
/// environment directly. Starting a process with the environment edited means
/// <c>bInheritHandles</c>, and that inherits the whole table, not only what was redirected:
/// a terminal launched from <c>SessionCli new</c> held our stdout open for as long as the
/// operator left the window up, so a caller reading our JSON waited for an end of file that
/// only came when they closed the session we had just opened for them. ShellExecute passes
/// no handles at all.
///
/// That moves the environment edit into the command line. If this process was itself
/// launched from a Claude session it inherited that session's markers, and passing them on
/// makes the new session think it is a nested child and skip saving its transcript — so
/// <c>cmd</c> clears them before it starts anything.
/// </summary>
public static class TerminalLauncher
{
    /// <summary>Markers a session exports to its children, which a new session must not see.</summary>
    private static readonly string[] Inherited = ["CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID"];

    public static void Start(string command)
    {
        var clear = string.Concat(Inherited.Select(name => $"set \"{name}=\" & "));

        // The command travels inside a quoted argument, so a double quote of its own would
        // end it early — the tail of the line would then be read by cmd as commands of its
        // own. Callers here quote with apostrophes (see SessionName.Quote) and so never hit
        // this, but a session named from a model-written title is one edit away from doing.
        var quoted = command.Replace("\"", "\\\"");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {clear}start \"\" powershell.exe -NoExit -Command \"{quoted}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,   // cmd's own window; `start` still gives PowerShell one
        })?.Dispose();
    }
}
