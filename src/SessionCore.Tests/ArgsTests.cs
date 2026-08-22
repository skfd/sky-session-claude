using SessionCli;

namespace SessionCore.Tests;

/// <summary>
/// The command line is written by agents and shell scripts as often as by hand, so both
/// spellings of a flag have to work and an unrecognised one has to be an error rather than
/// something quietly ignored — `restart --dryrun` must not restart anything.
/// </summary>
public class ArgsTests
{
    private static Args Parse(params string[] argv) => new(argv[0], argv.Skip(1));

    [Fact]
    public void ReadsPositionalArguments()
    {
        var args = Parse("done", "abc", "def");
        Assert.Equal(new[] { "abc", "def" }, args.Positional);
        Assert.Equal("done", args.Verb);
    }

    [Fact]
    public void ReadsSpaceSeparatedFlagValues()
    {
        Assert.Equal("waiting-you", Parse("list", "--status", "waiting-you").Value("status"));
    }

    [Fact]
    public void ReadsEqualsSeparatedFlagValues()
    {
        Assert.Equal("waiting-you", Parse("list", "--status=waiting-you").Value("status"));
    }

    [Fact]
    public void TreatsAFlagWithNoValueAsASwitch()
    {
        var args = Parse("restart", "--stale", "--yes");
        Assert.True(args.Has("stale"));
        Assert.True(args.Has("yes"));
        Assert.Null(args.Value("stale"));
    }

    // The value of --status must not swallow the flag that follows it.
    [Fact]
    public void ASwitchDoesNotEatTheNextFlag()
    {
        var args = Parse("list", "--live", "--status", "complete");
        Assert.True(args.Has("live"));
        Assert.Equal("complete", args.Value("status"));
    }

    [Fact]
    public void FlagsAreCaseInsensitive()
    {
        Assert.True(Parse("restart", "--STALE").Has("stale"));
    }

    [Fact]
    public void PositionalsSurviveFlagsAroundThem()
    {
        var args = Parse("done", "--dry-run", "abc", "def");
        Assert.Equal(new[] { "abc", "def" }, args.Positional);
        Assert.True(args.Has("dry-run"));
    }

    [Fact]
    public void IntFallsBackWhenTheFlagIsAbsent()
    {
        Assert.Equal(50, Parse("list").Int("top", 50));
        Assert.Equal(7, Parse("list", "--top", "7").Int("top", 50));
    }

    [Fact]
    public void IntRejectsSomethingThatIsNotANumber()
    {
        Assert.Throws<UsageException>(() => Parse("list", "--top", "lots").Int("top", 50));
    }

    [Fact]
    public void IntRejectsABareFlagThatNeedsAValue()
    {
        Assert.Throws<UsageException>(() => Parse("list", "--top").Int("top", 50));
    }

    [Fact]
    public void RequireRejectsAMissingValue()
    {
        Assert.Throws<UsageException>(() => Parse("list", "--json").Require("json"));
    }

    // A typo in a flag must stop the verb, not be dropped on the floor: --dryrun silently
    // ignored is the difference between a plan and a dozen restarted terminals.
    [Fact]
    public void RejectsAnUnknownFlag()
    {
        var args = Parse("restart", "--dryrun");
        Assert.Throws<UsageException>(() => args.RejectUnknown("stale", "yes", "force", "dry-run"));
    }

    [Fact]
    public void AcceptsKnownFlags()
    {
        var args = Parse("restart", "--stale", "--yes");
        args.RejectUnknown("stale", "yes", "force", "dry-run");   // does not throw
    }
}
