using System.Diagnostics;

namespace SessionCore;

/// <summary>One tab standby wants in the window: where it opens, what it is called, what it runs.</summary>
public sealed record TerminalTab(string Folder, string Title, string Command);

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

    /// <summary>
    /// The Windows Terminal window standby's hosts share. A name rather than a handle, because
    /// a name is all <c>wt -w</c> needs: the first tab makes the window and every later one
    /// finds it — including the ones tomorrow's standby opens, which join today's window if it
    /// is still up.
    /// </summary>
    public const string StandbyWindow = "sky-standby";

    /// <summary>
    /// Whether tabs are available at all. Callers word what they promise from this: standby
    /// opens one window with a tab per project where Windows Terminal is installed, and the
    /// old window per project where it is not.
    /// </summary>
    public static bool HasWindowsTerminal => FindWindowsTerminal() is not null;

    /// <summary>One command in a terminal of its own — a session, a resume, a fork.</summary>
    public static void Start(string command) => Run(WindowArguments(command));

    /// <summary>
    /// Open every host of a standby run as a tab in one window — the difference between a
    /// desktop you can still use afterwards and sixteen console windows to alt-tab through.
    ///
    /// It is one <c>wt</c> invocation for all of them rather than one per tab, and that is the
    /// point rather than an optimisation: <c>-w &lt;name&gt;</c> creates the window when no
    /// window wears the name, so sixteen invocations fired at once all look, all miss, and all
    /// create — and the desktop ends up with four windows instead of one. One command line
    /// asks once. Only a run long enough to overflow a command line is split, and the pieces
    /// after the first wait for the window they are joining.
    ///
    /// The console under each tab is a real one, which is what keeps the rest of this app
    /// working: <see cref="HostRestarter"/> walks to the PowerShell underneath a host and types
    /// the relaunch into it, the exit gesture goes into that same console, and the folder-trust
    /// prompt has somewhere to appear where it can be answered. A host with no console at all
    /// would cost all three, which is why hiding them is not the same as removing them.
    /// </summary>
    public static void StartTabs(IReadOnlyList<TerminalTab> tabs, string window)
    {
        if (tabs.Count == 0) return;

        if (FindWindowsTerminal() is null)
        {
            foreach (var tab in tabs)
                Start($"cd {SessionName.Quote(tab.Folder)}; {tab.Command}");
            return;
        }

        var first = true;
        foreach (var batch in Batch(tabs, window))
        {
            // Only the first batch can create the window; the rest join one that was asked for
            // milliseconds ago and may not exist yet. The wait is cheap and only ever runs on
            // a sweep of thirty-odd projects.
            if (!first) Thread.Sleep(1500);
            Run(batch);
            first = false;
        }
    }

    /// <summary>The <c>cmd</c> line that opens one command in a window of its own.</summary>
    public static string WindowArguments(string command) =>
        $"/c {Clear()}start \"\" powershell.exe -NoExit -Command \"{Escape(command)}\"";

    /// <summary>
    /// The <c>cmd</c> line that opens a run of tabs in the named window.
    ///
    /// <c>--title</c> is what makes the tab strip readable — sixteen tabs called "PowerShell"
    /// are not a list of your projects. Pinning it is safe here in a way it would not be for a
    /// session: the app finds a session's tab by the title the CLI paints into it, and a host's
    /// tab is never looked for that way, a host having no session of its own to look up.
    ///
    /// The <c>;</c> between tabs is Windows Terminal's own argument separator, and it passes
    /// through <c>cmd</c> untouched — unlike <c>&amp;</c>, it means nothing to the shell. It is
    /// also why nothing composed into a tab may carry one: the folder arrives quoted, and the
    /// command is <see cref="ClaudeLaunch.Host"/>, which has none.
    /// </summary>
    public static string TabArguments(IEnumerable<TerminalTab> tabs, string window) =>
        $"/c {Clear()}start \"\" wt.exe -w \"{window}\" "
        + string.Join(" ; ", tabs.Select(tab =>
            $"new-tab --title \"{Escape(tab.Title)}\" -d \"{Folder(tab.Folder)}\""
            + $" powershell.exe -NoExit -Command \"{Escape(tab.Command)}\""));

    /// <summary>
    /// Command lines short enough for <c>cmd</c>, which stops at 8191 characters and truncates
    /// rather than complains — a limit twenty-odd projects can reach, and a truncated line
    /// would start the last host with half a command.
    /// </summary>
    private static IEnumerable<string> Batch(IReadOnlyList<TerminalTab> tabs, string window)
    {
        const int budget = 7000;

        var pending = new List<TerminalTab>();
        foreach (var tab in tabs)
        {
            pending.Add(tab);
            if (TabArguments(pending, window).Length <= budget) continue;

            // A single tab over the budget has nothing to fall back to, so it goes out alone
            // and is left to cmd — a long line is a better answer than a dropped project.
            if (pending.Count == 1)
            {
                yield return TabArguments(pending, window);
                pending.Clear();
                continue;
            }

            pending.RemoveAt(pending.Count - 1);
            yield return TabArguments(pending, window);
            pending.Clear();
            pending.Add(tab);
        }

        if (pending.Count > 0) yield return TabArguments(pending, window);
    }

    private static void Run(string arguments) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,   // cmd's own window; `start` still gives a terminal one
        })?.Dispose();

    /// <summary>The environment edit, written as commands because the launch inherits nothing.</summary>
    private static string Clear() =>
        string.Concat(Inherited.Select(name => $"set \"{name}=\" & "));

    /// <summary>
    /// The command travels inside a quoted argument, so a double quote of its own would end it
    /// early — the tail of the line would then be read by cmd as commands of its own. Callers
    /// here quote with apostrophes (see <see cref="SessionName.Quote"/>) and so never hit this,
    /// but a session named from a model-written title is one edit away from doing.
    /// </summary>
    private static string Escape(string value) => value.Replace("\"", "\\\"");

    /// <summary>
    /// A folder as it can be handed to <c>-d "…"</c>. A trailing backslash would escape the
    /// closing quote and swallow the rest of the line; it is only ever a drive root, and
    /// <c>C:\</c> becomes <c>C:\.</c> — the same folder, spelled so it can be quoted.
    /// </summary>
    private static string Folder(string folder) =>
        folder.EndsWith('\\') ? folder + "." : folder;

    /// <summary>
    /// Windows Terminal, or nothing. It ships as an app execution alias under
    /// <c>WindowsApps</c> — a zero-length reparse point <c>File.Exists</c> still sees — and
    /// that folder is on the PATH of an interactive session but not always of a process started
    /// from a scheduled task, so both are asked.
    /// </summary>
    private static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(alias)) return alias;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(dir, "wt.exe"); }
            catch (ArgumentException) { continue; }   // a PATH entry with characters a path cannot hold
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
