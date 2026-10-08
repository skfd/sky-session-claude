using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The keeper runs every minute with nobody watching, so the thing to get wrong is not
/// whether it opens a host but how often: a host that cannot come up must not buy a relaunch
/// a minute, and a folder never trusted must not be granted by a timer.
/// </summary>
public class StandbyKeeperTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0);

    private static StandbyPlan PlanFor(params StandbyTarget[] open) => new() { Open = open, Skipped = [] };

    private static StandbyTarget Target(string folder, bool needsTrust = false, DeadHostShell? reviveIn = null) => new()
    {
        Folder = folder,
        Project = Standby.ProjectOf(folder),
        LastActive = Now.AddDays(-1),
        NeedsTrust = needsTrust,
        ReviveIn = reviveIn,
    };

    [Fact]
    public void AFolderNeverTriedIsOpenedAtOnce()
    {
        var keeper = new StandbyKeeper();

        Assert.Single(keeper.Hold(PlanFor(Target(@"C:\Code\sky")), Now, mayGrantTrust: false).Open);
    }

    [Fact]
    public void AFolderJustTriedWaits()
    {
        var keeper = new StandbyKeeper();
        keeper.Attempted([@"C:\Code\sky"], Now);

        var plan = keeper.Hold(PlanFor(Target(@"C:\Code\sky")), Now.AddMinutes(1), mayGrantTrust: false);

        Assert.Empty(plan.Open);
        Assert.Contains("trying again in 1 min", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void EachAttemptWaitsTwiceAsLongUpToAnHour()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), StandbyKeeper.WaitAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(4), StandbyKeeper.WaitAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(32), StandbyKeeper.WaitAfter(5));
        Assert.Equal(TimeSpan.FromHours(1), StandbyKeeper.WaitAfter(6));
        Assert.Equal(TimeSpan.FromHours(1), StandbyKeeper.WaitAfter(500));
    }

    [Fact]
    public void AHostThatKeepsDyingIsTriedLessOften()
    {
        var keeper = new StandbyKeeper();
        keeper.Attempted([@"C:\Code\sky"], Now);
        keeper.Attempted([@"C:\Code\sky"], Now.AddMinutes(2));

        Assert.NotNull(keeper.WaitLeft(@"C:\Code\sky", Now.AddMinutes(5)));
        Assert.Null(keeper.WaitLeft(@"C:\Code\sky", Now.AddMinutes(6)));
    }

    /// <summary>
    /// Up for half an hour means up: the next time it goes down is a first time, not the
    /// seventh, and it is put back in two minutes rather than an hour.
    /// </summary>
    [Fact]
    public void AHostThatStayedUpStartsAgainFromTheShortWait()
    {
        var keeper = new StandbyKeeper();
        for (var i = 0; i < 6; i++) keeper.Attempted([@"C:\Code\sky"], Now);

        keeper.Hold(PlanFor(), Now.AddMinutes(30), mayGrantTrust: false);   // up, not in the plan
        keeper.Attempted([@"C:\Code\sky"], Now.AddMinutes(40));

        Assert.Null(keeper.WaitLeft(@"C:\Code\sky", Now.AddMinutes(42)));
    }

    [Fact]
    public void AnUntrustedFolderWaitsForSomeoneToSaySo()
    {
        var keeper = new StandbyKeeper();
        var plan = PlanFor(Target(@"C:\Code\fresh", needsTrust: true));

        Assert.Empty(keeper.Hold(plan, Now, mayGrantTrust: false).Open);
        Assert.Single(keeper.Hold(plan, Now, mayGrantTrust: true).Open);
    }

    /// <summary>
    /// A held folder's dead tab is not closed: the next attempt wants it, and closing it now
    /// would mean a new tab then.
    /// </summary>
    [Fact]
    public void AHeldFoldersDeadTabIsKeptForNextTime()
    {
        var keeper = new StandbyKeeper();
        keeper.Attempted([@"C:\Code\sky"], Now);
        var shell = new DeadHostShell { Pid = 10, Folder = @"C:\Code\sky", HostCommand = "claude rc" };

        var plan = keeper.Hold(PlanFor(Target(@"C:\Code\sky", reviveIn: shell)), Now.AddMinutes(1), mayGrantTrust: false);

        Assert.Empty(plan.Open);
        Assert.Empty(plan.Close);
    }
}
