using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The command line standby's hosts arrive on. Sixteen projects used to mean sixteen console
/// windows, which is a desktop you cannot use afterwards; they are one Windows Terminal window
/// with a tab each now, and everything here is about that line being one line.
/// </summary>
public class StandbyWindowTests
{
    private static TerminalTab Tab(string project) =>
        new($@"C:\Users\kk\Code\{project}", project, ClaudeLaunch.Host(project));

    /// <summary>
    /// The whole point: one <c>wt</c> invocation, one window, one tab per project. Sixteen
    /// invocations would race to create the named window and leave several of them.
    /// </summary>
    [Fact]
    public void PutsEveryHostInOneWindow()
    {
        var line = TerminalLauncher.TabArguments(
            [Tab("sky-session-claude"), Tab("address-vault"), Tab("cowork")],
            TerminalLauncher.StandbyWindow);

        Assert.Single(Occurrences(line, "start \"\" wt.exe"));
        Assert.Equal(3, Occurrences(line, "new-tab").Count);
        Assert.Contains("-w \"sky-standby\"", line);
        Assert.Equal(2, Occurrences(line, " ; ").Count);   // the separator, between tabs only
    }

    /// <summary>
    /// The folder goes in under <c>-d</c> rather than a <c>cd</c> in front of the command, and
    /// that is not cosmetic: <c>;</c> is Windows Terminal's own argument separator, so
    /// <c>cd 'x'; claude rc</c> would be read as the end of one tab and the start of another.
    /// </summary>
    [Fact]
    public void TakesTheFolderAsAnArgumentRatherThanACd()
    {
        var line = TerminalLauncher.TabArguments([Tab("cowork")], TerminalLauncher.StandbyWindow);

        Assert.Contains(@"-d ""C:\Users\kk\Code\cowork""", line);
        Assert.DoesNotContain("cd ", line);
    }

    /// <summary>Sixteen tabs called "PowerShell" are not a list of your projects.</summary>
    [Fact]
    public void NamesEachTabAfterItsProject()
    {
        var line = TerminalLauncher.TabArguments([Tab("xrm-ribbon")], TerminalLauncher.StandbyWindow);

        Assert.Contains("new-tab --title \"xrm-ribbon\"", line);
    }

    /// <summary>
    /// The host is a host, tab or no tab: the session name prefix has to survive the move, or
    /// every row on the phone goes back to being named after this machine.
    /// </summary>
    [Fact]
    public void StillRunsTheHostWithItsPrefix()
    {
        var line = TerminalLauncher.TabArguments([Tab("battle-agents")], TerminalLauncher.StandbyWindow);

        Assert.Contains(
            "powershell.exe -NoExit -Command \"claude rc --remote-control-session-name-prefix"
            + " 'battle-agents' --no-create-session-in-dir --spawn=same-dir\"",
            line);
    }

    /// <summary>The markers a session hands its children are cleared here as anywhere else.</summary>
    [Fact]
    public void ClearsTheInheritedSessionMarkers()
    {
        var line = TerminalLauncher.TabArguments([Tab("cowork")], TerminalLauncher.StandbyWindow);

        Assert.StartsWith("/c set \"CLAUDE_CODE_CHILD_SESSION=\" & set \"CLAUDE_CODE_SESSION_ID=\" & start", line);
    }

    /// <summary>
    /// A drive root is the one folder that ends in a backslash, and <c>-d "C:\"</c> would
    /// escape its own closing quote and swallow the rest of the line — every tab after it.
    /// </summary>
    [Fact]
    public void SpellsADriveRootSoItCanBeQuoted()
    {
        var line = TerminalLauncher.TabArguments(
            [new TerminalTab(@"C:\", "C", ClaudeLaunch.Host("C"))],
            TerminalLauncher.StandbyWindow);

        Assert.Contains(@"-d ""C:\.""", line);
        Assert.DoesNotContain(@"-d ""C:\""", line);
    }

    /// <summary>A window per project is still what a machine without Windows Terminal gets.</summary>
    [Fact]
    public void KeepsTheOldLineForOneWindow()
    {
        Assert.Contains(
            "start \"\" powershell.exe -NoExit -Command \"cd 'C:\\Code\\sky'; claude rc",
            TerminalLauncher.WindowArguments(LaunchLine.HostIn(@"C:\Code\sky", "sky")));
    }

    private static List<int> Occurrences(string haystack, string needle)
    {
        var found = new List<int>();
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            found.Add(i);
        return found;
    }
}
