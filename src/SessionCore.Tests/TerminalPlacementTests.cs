using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// New terminals open on the second screen, where the operator keeps every terminal, instead
/// of on the primary one where Windows puts them and they get dragged across by hand.
/// </summary>
public class TerminalPlacementTests
{
    // The desk as it stands: the laptop shut, the primary screen on the right, the
    // terminals' screen to its left.
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1032, Primary: true);
    private static readonly ScreenArea Left = new(-1920, 0, 0, 1032, Primary: false);

    [Fact]
    public void OpensOnTheScreenThatIsNotPrimary()
    {
        Assert.Equal((-1920 + 26, 26), TerminalPlacement.Choose([Primary, Left], []));
    }

    [Fact]
    public void LeavesAOneScreenDeskToWindows()
    {
        Assert.Null(TerminalPlacement.Choose([Primary], []));
    }

    /// <summary>Five sessions in a row are five windows, not one with four exactly underneath.</summary>
    [Fact]
    public void StepsDownPastTheTerminalsAlreadyThere()
    {
        var two = new List<(int, int)> { (-1300, 400), (-1250, 450), (900, 500) };   // the last is on the primary

        Assert.Equal((-1920 + 3 * 26, 3 * 26), TerminalPlacement.Choose([Primary, Left], two));
    }

    [Fact]
    public void StartsTheCascadeAgainAfterEightSteps()
    {
        var eight = Enumerable.Range(0, 8).Select(i => (-1500 + i, 400)).ToList();

        Assert.Equal((-1920 + 26, 26), TerminalPlacement.Choose([Primary, Left], eight));
    }

    /// <summary>
    /// With the laptop panel open as well there are two screens that are not primary, and the
    /// one already holding the terminals is the one meant.
    /// </summary>
    [Fact]
    public void PrefersTheSecondScreenThatAlreadyHoldsTheTerminals()
    {
        var laptop = new ScreenArea(1920, 200, 3456, 1100, Primary: false);
        var terminals = new List<(int, int)> { (-1000, 500) };

        Assert.Equal((-1920 + 2 * 26, 2 * 26), TerminalPlacement.Choose([Primary, laptop, Left], terminals));
        Assert.Equal((1920 + 26, 200 + 26), TerminalPlacement.Choose([Primary, laptop, Left], []));
    }

    /// <summary>
    /// The shell under a placed window keeps the command line <see cref="HostShells"/> reads,
    /// with the <c>;</c> escaped for Windows Terminal rather than the whole thing encoded.
    /// </summary>
    [Fact]
    public void APlacedWindowKeepsAReadableCommand()
    {
        var line = TerminalLauncher.WindowArguments(LaunchLine.HostIn(@"C:\Code\sky", "sky"), "-1894,26");

        Assert.Contains("start \"\" wt.exe -w new --pos -1894,26 new-tab -d \"", line);
        Assert.Contains("powershell.exe -NoExit -Command \"cd 'C:\\Code\\sky'\\; claude rc", line);
        Assert.DoesNotContain("EncodedCommand", line);
    }

    /// <summary>What arrives once Windows Terminal has unescaped it is still a host's command.</summary>
    [Fact]
    public void ThePlacedHostIsStillFoundAsAHost()
    {
        var line = TerminalLauncher.WindowArguments(LaunchLine.HostIn(@"C:\Code\sky", "sky"), "-1894,26");
        var shell = "powershell.exe " + line[line.IndexOf("-NoExit", StringComparison.Ordinal)..].Replace("\\;", ";");

        Assert.NotNull(HostShells.HostCommandIn(shell));
    }

    [Fact]
    public void APromptCmdWouldReadStillGoesInEncoded()
    {
        var line = TerminalLauncher.WindowArguments(LaunchLine.NewIn(@"C:\Code\sky", prompt: "fix 100% of it & ship"), "-1894,26");

        Assert.Contains("-EncodedCommand ", line);
    }

    [Fact]
    public void AStandbyWindowIsPlacedToo()
    {
        var line = TerminalLauncher.TabArguments(
            [new TerminalTab(@"C:\Code\sky", "sky", ClaudeLaunch.Host("sky"))],
            TerminalLauncher.StandbyWindow, "-1894,26");

        Assert.Contains("wt.exe -w \"sky-standby\" --pos -1894,26 new-tab", line);
    }
}
