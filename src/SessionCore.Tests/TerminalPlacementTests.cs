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

    /// <summary>
    /// A terminal as Windows reports it once opened at <paramref name="x"/>,<paramref name="y"/>:
    /// the rectangle starts behind an invisible border, 8 pixels left of the visible edge.
    /// </summary>
    private static WindowBox At(int x, int y) => new(x - 8, y, x - 8 + 1129, y + 635);

    /// <summary>The <paramref name="i"/>th slot of the cascade on the left screen.</summary>
    private static (int, int) Slot(int i) => (-1920 + 26 * (i + 1), 26 * (i + 1));

    [Fact]
    public void OpensOnTheScreenThatIsNotPrimary()
    {
        Assert.Equal(Slot(0), TerminalPlacement.Choose([Primary, Left], []));
    }

    [Fact]
    public void LeavesAOneScreenDeskToWindows()
    {
        Assert.Null(TerminalPlacement.Choose([Primary], []));
    }

    /// <summary>Five sessions in a row are five windows, not one with four exactly underneath.</summary>
    [Fact]
    public void TakesTheNextSlotPastTheTerminalsAlreadyThere()
    {
        var (x0, y0) = Slot(0);
        var (x1, y1) = Slot(1);

        Assert.Equal(Slot(2), TerminalPlacement.Choose([Primary, Left], [At(x0, y0), At(x1, y1)]));
    }

    /// <summary>
    /// A count would land on a window already there whenever an earlier one was closed or
    /// dragged off: two windows, one of them in slot 2, and the third slot by count is taken.
    /// </summary>
    [Fact]
    public void FillsTheFirstGapRatherThanCounting()
    {
        var (x0, y0) = Slot(0);
        var (x2, y2) = Slot(2);

        Assert.Equal(Slot(1), TerminalPlacement.Choose([Primary, Left], [At(x0, y0), At(x2, y2)]));
    }

    /// <summary>A window dragged somewhere of the operator's own takes no slot.</summary>
    [Fact]
    public void AWindowOffTheLatticeTakesNoSlot()
    {
        Assert.Equal(Slot(0), TerminalPlacement.Choose([Primary, Left], [At(-1300, 400), At(900, 500)]));
    }

    [Fact]
    public void GoesRoundAgainWhenEverySlotIsTaken()
    {
        var full = Enumerable.Range(0, 9).Select(i => Slot(i % 8)).Select(s => At(s.Item1, s.Item2)).ToList();

        Assert.Equal(Slot(1), TerminalPlacement.Choose([Primary, Left], full));   // nine windows, 9 % 8
    }

    /// <summary>
    /// With the laptop panel open as well there are two screens that are not primary, and the
    /// one already holding the terminals is the one meant.
    /// </summary>
    [Fact]
    public void PrefersTheSecondScreenThatAlreadyHoldsTheTerminals()
    {
        var laptop = new ScreenArea(1920, 200, 3456, 1100, Primary: false);

        Assert.Equal(Slot(0), TerminalPlacement.Choose([Primary, laptop, Left], [At(-1300, 400)]));
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
