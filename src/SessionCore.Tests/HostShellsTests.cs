using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A dead host is a tab that still looks alive: the shell standby opened stays at a prompt
/// after the host under it exits, titled after the project. What can go wrong is reading
/// somebody's own terminal as one of these — and then typing <c>exit</c> into it — so the
/// tests below hold both edges: what a host's shell looks like, and that a shell running
/// anything at all is left alone.
/// </summary>
public class HostShellsTests
{
    private const string TabShell =
        "powershell.exe -NoExit -Command \"claude rc --remote-control-session-name-prefix 'cowork' --no-create-session-in-dir --spawn=same-dir\"";

    private const string WindowShell =
        "powershell.exe -NoExit -Command \"cd 'C:\\Code\\cowork'; claude rc --remote-control-session-name-prefix 'cowork' --no-create-session-in-dir --spawn=same-dir\"";

    [Fact]
    public void ReadsTheHostOutOfATabShell() =>
        Assert.Equal(
            "claude rc --remote-control-session-name-prefix 'cowork' --no-create-session-in-dir --spawn=same-dir",
            HostShells.HostCommandIn(TabShell));

    [Fact]
    public void ReadsTheHostOutOfAWindowShellPastItsCd() =>
        Assert.StartsWith("claude rc ", HostShells.HostCommandIn(WindowShell));

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("powershell.exe -NoExit -Command \"claude --resume 1234 --remote-control\"")]
    [InlineData("powershell.exe -NoExit -Command \"claude rcx\"")]
    [InlineData("powershell.exe -NoExit -Command \"cd 'C:\\x'; git status\"")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    public void AnythingButAHostIsNotOne(string commandLine) =>
        Assert.Null(HostShells.HostCommandIn(commandLine));

    private static IReadOnlyList<DeadHostShell> Dead(
        Dictionary<int, List<ProcRef>> tree, Dictionary<int, string> commandLines) =>
        HostShells.Dead(tree,
            pid => commandLines.GetValueOrDefault(pid),
            pid => pid switch { 10 => @"C:\Code\cowork\", 20 => @"C:\Code\sky\", _ => @"C:\Code\other\" });

    [Fact]
    public void AHostShellWithNothingUnderItIsDead()
    {
        var tree = new Dictionary<int, List<ProcRef>>
        {
            [1] = [new(10, "powershell.exe"), new(11, "OpenConsole.exe")],
        };

        var dead = Assert.Single(Dead(tree, new() { [10] = TabShell }));

        Assert.Equal(10, dead.Pid);
        Assert.Equal(@"C:\Code\cowork", dead.Folder);
        Assert.Equal("cowork", dead.Project);
    }

    /// <summary>The host itself under the shell, or anything typed at it afterwards.</summary>
    [Theory]
    [InlineData("claude.exe")]
    [InlineData("git.exe")]
    public void AShellRunningAnythingIsLeftAlone(string child)
    {
        var tree = new Dictionary<int, List<ProcRef>>
        {
            [1] = [new(10, "powershell.exe")],
            [10] = [new(12, "conhost.exe"), new(13, child)],
        };

        Assert.Empty(Dead(tree, new() { [10] = TabShell }));
    }

    [Fact]
    public void AnOrdinaryShellIsNotAHostsEvenWhenIdle()
    {
        var tree = new Dictionary<int, List<ProcRef>> { [1] = [new(20, "powershell.exe")] };

        Assert.Empty(Dead(tree, new() { [20] = "powershell.exe" }));
    }
}

/// <summary>
/// Every dead tab goes exactly one place: back into service for its folder, or closed. The
/// failure this replaces is the third option — left where it was while a new tab opened
/// beside it — which is how the standby window came to hold nine corpses and seven of their
/// replacements.
/// </summary>
public class StandbyReuseTests
{
    private static StandbyTarget Target(string folder) => new()
    {
        Folder = folder,
        Project = Standby.ProjectOf(folder),
        LastActive = new DateTime(2026, 10, 8),
    };

    private static DeadHostShell Shell(int pid, string folder) =>
        new() { Pid = pid, Folder = folder, HostCommand = "claude rc" };

    [Fact]
    public void AHostIsPutBackInTheTabItsLastOneDiedIn()
    {
        var plan = Standby.Reuse(
            new StandbyPlan { Open = [Target(@"C:\Code\sky")], Skipped = [] },
            [Shell(10, @"C:\Code\sky\")]);

        Assert.Equal(10, Assert.Single(plan.Open).ReviveIn?.Pid);
        Assert.Empty(plan.Close);
    }

    [Fact]
    public void ADeadTabNothingWantsIsClosed()
    {
        var plan = Standby.Reuse(
            new StandbyPlan { Open = [Target(@"C:\Code\sky")], Skipped = [] },
            [Shell(10, @"C:\Code\cowork")]);

        Assert.Null(Assert.Single(plan.Open).ReviveIn);
        Assert.Equal(10, Assert.Single(plan.Close).Pid);
    }

    [Fact]
    public void ASecondDeadTabForTheSameFolderIsClosedNotKept()
    {
        var plan = Standby.Reuse(
            new StandbyPlan { Open = [Target(@"C:\Code\sky")], Skipped = [] },
            [Shell(30, @"C:\Code\sky"), Shell(10, @"C:\Code\sky")]);

        Assert.Equal(10, Assert.Single(plan.Open).ReviveIn?.Pid);
        Assert.Equal(30, Assert.Single(plan.Close).Pid);
    }

    /// <summary>A folder with a live host is not in Open, so its dead duplicates all go.</summary>
    [Fact]
    public void EveryDeadTabGoesWhenNothingNeedsOpening()
    {
        var plan = Standby.Reuse(
            new StandbyPlan { Open = [], Skipped = [] },
            [Shell(10, @"C:\Code\sky"), Shell(20, @"C:\Code\cowork")]);

        Assert.Equal([10, 20], plan.Close.Select(s => s.Pid).Order());
    }
}
