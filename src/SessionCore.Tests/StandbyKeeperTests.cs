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

    /// <summary>The same plan seen twice — what a host that is really gone looks like.</summary>
    private static StandbyPlan HoldTwice(StandbyKeeper keeper, StandbyPlan plan, DateTime now, bool mayGrantTrust = false)
    {
        keeper.Hold(plan, now.AddMinutes(-1), mayGrantTrust);
        return keeper.Hold(plan, now, mayGrantTrust);
    }

    [Fact]
    public void AFolderNeverTriedIsOpenedOnTheSecondLook()
    {
        var keeper = new StandbyKeeper();
        var plan = PlanFor(Target(@"C:\Code\sky"));

        Assert.Empty(keeper.Hold(plan, Now, mayGrantTrust: false).Open);
        Assert.Single(keeper.Hold(plan, Now.AddMinutes(1), mayGrantTrust: false).Open);
    }

    /// <summary>
    /// A restart from another process quits a host and types its relaunch a second or two
    /// later. A look in that gap sees a missing host and an idle shell; acting on it would type
    /// a second relaunch into the shell, or exit it.
    /// </summary>
    [Fact]
    public void AHostSeenGoneOnceIsLeftToWhateverIsRestartingIt()
    {
        var keeper = new StandbyKeeper();
        var shell = new DeadHostShell { Pid = 10, Folder = @"C:\Code\old", HostCommand = "claude rc" };
        var plan = new StandbyPlan { Open = [Target(@"C:\Code\sky")], Skipped = [], Close = [shell] };

        var first = keeper.Hold(plan, Now, mayGrantTrust: false);

        Assert.Empty(first.Open);
        Assert.Empty(first.Close);
    }

    [Fact]
    public void ADeadTabSeenTwiceIsClosed()
    {
        var keeper = new StandbyKeeper();
        var shell = new DeadHostShell { Pid = 10, Folder = @"C:\Code\old", HostCommand = "claude rc" };

        var plan = HoldTwice(keeper, new StandbyPlan { Open = [], Skipped = [], Close = [shell] }, Now);

        Assert.Equal(10, Assert.Single(plan.Close).Pid);
    }

    /// <summary>Gone, back, gone again: the second disappearance is a first look, not a second.</summary>
    [Fact]
    public void ALookWithEverythingUpForgetsWhatWasMissing()
    {
        var keeper = new StandbyKeeper();
        var plan = PlanFor(Target(@"C:\Code\sky"));
        keeper.Hold(plan, Now, mayGrantTrust: false);

        keeper.AllUp();

        Assert.Empty(keeper.Hold(plan, Now.AddMinutes(10), mayGrantTrust: false).Open);
    }

    [Fact]
    public void AFolderJustTriedWaits()
    {
        var keeper = new StandbyKeeper();
        keeper.Attempted([@"C:\Code\sky"], Now);

        var plan = HoldTwice(keeper, PlanFor(Target(@"C:\Code\sky")), Now.AddMinutes(1));

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

        Assert.Empty(HoldTwice(keeper, plan, Now).Open);
        Assert.Single(HoldTwice(keeper, plan, Now, mayGrantTrust: true).Open);
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

        var plan = HoldTwice(keeper, PlanFor(Target(@"C:\Code\sky", reviveIn: shell)), Now.AddMinutes(1));

        Assert.Empty(plan.Open);
        Assert.Empty(plan.Close);
    }
}
