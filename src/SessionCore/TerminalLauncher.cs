using System.Diagnostics;

namespace SessionCore;

/// <summary>
/// Open a terminal and run a command in it — the one way this app starts a session, whether
/// the ask came from a double-click, a CLI verb, or an agent.
///
/// The command goes through <c>cmd /c start</c> rather than straight to PowerShell, because
/// Claude Code needs a console of its own. A child started directly inherits its parent's:
/// harmless from the window (a GUI process has none, so Windows makes a fresh one), fatal
/// from the CLI, where the console it inherits is whatever the caller had. An agent's shell
/// has a redirected stdin, and Claude Code reads that as <c>--print</c> mode and exits with
/// "Input must be provided either through stdin or as a prompt argument" before the operator
/// sees anything. <c>start</c> always makes a new console, so the session gets a real
/// terminal no matter who asked for it.
///
/// <c>cmd</c> itself is given no window (<see cref="ProcessStartInfo.CreateNoWindow"/>) so
/// the window path does not flash one; that flag does not reach the console <c>start</c>
/// makes for PowerShell. And <c>cmd</c>'s own metacharacters (<c>&amp;</c>, <c>^</c>) are
/// inert here: the command always contains a space, so .NET quotes it as one argument.
///
/// If this process was itself launched from a Claude session it inherited that session's
/// markers; passing them on makes the new session think it is a nested child and skip
/// saving its transcript. UseShellExecute must be false to edit the child environment at all.
/// </summary>
public static class TerminalLauncher
{
    public static void Start(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "start", "", "powershell.exe", "-NoExit", "-Command", command },
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Remove("CLAUDE_CODE_CHILD_SESSION");
        psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        Process.Start(psi);
    }
}
