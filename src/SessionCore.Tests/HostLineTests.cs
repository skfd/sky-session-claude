using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// What `standby` types into a terminal. The prefix is the whole reason this is not just
/// `claude rc`: it is what every session the host creates is named after, and it is the only
/// thing standing between a phone list of your repos and a phone list of your hostname.
/// </summary>
public class HostLineTests
{
    [Fact]
    public void NamesEverySessionTheHostMakesAfterTheProject()
    {
        Assert.Equal(
            "claude rc --remote-control-session-name-prefix 'sky-session-claude' --create-session-in-dir",
            ClaudeLaunch.Host("sky-session-claude"));
    }

    /// <summary>
    /// Without a prefix the host falls back to the machine's hostname, and the rows read
    /// `cc-pc-sorted-stallman` — observed on claude.ai/code, and useless for picking a repo.
    /// The line is still valid, so this is a shape worth keeping rather than forbidding.
    /// </summary>
    [Fact]
    public void LeavesThePrefixOffWhenThereIsNone()
    {
        Assert.Equal("claude rc --create-session-in-dir", ClaudeLaunch.Host());
        Assert.Equal("claude rc --create-session-in-dir", ClaudeLaunch.Host(""));
    }

    [Fact]
    public void GoesIntoTheFolderFirst()
    {
        Assert.Equal(
            @"cd 'C:\Code\sky'; claude rc --remote-control-session-name-prefix 'sky' --create-session-in-dir",
            LaunchLine.HostIn(@"C:\Code\sky", "sky"));
    }

    // Single quotes are how PowerShell is told to take a value literally, so a repo with one
    // in its name has to double it or the rest of the line becomes code.
    [Fact]
    public void QuotesAFolderOrPrefixContainingAQuote()
    {
        Assert.Equal(
            @"cd 'C:\Code\kk''s repo'; claude rc --remote-control-session-name-prefix 'kk''s repo' --create-session-in-dir",
            LaunchLine.HostIn(@"C:\Code\kk's repo", "kk's repo"));
    }

    /// <summary>
    /// A host is not a session, and the difference has to survive in the command line: nothing
    /// here may pick up `--resume`, `--name` or a bare `--remote-control`, all of which belong
    /// to the other kind of launch.
    /// </summary>
    [Fact]
    public void IsAHostRatherThanASession()
    {
        var line = ClaudeLaunch.Host("sky");

        Assert.StartsWith("claude rc", line);
        Assert.DoesNotContain("--resume", line);
        Assert.DoesNotContain("--name ", line);
        Assert.Contains("--remote-control-session-name-prefix", line);
    }
}
