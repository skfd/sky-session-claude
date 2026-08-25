using SessionCli;

namespace SessionCore.Tests;

/// <summary>
/// `new` is the one verb that names no session, because the id it would name does not
/// exist yet. What can go wrong is the line it types into the terminal: a folder with a
/// space or an apostrophe in it, or a caller reaching for `new <id>` when they meant
/// `resume <id>`.
/// </summary>
public class NewSessionTests
{
    private static Args Parse(params string[] argv) => new("new", argv);

    [Fact]
    public void LaunchesInTheFolderWithNoNameOfItsOwn()
    {
        Assert.Equal(@"cd 'C:\Code\sky'; claude --remote-control", Commands.NewSessionLine(@"C:\Code\sky", null));
    }

    [Fact]
    public void PassesAChosenNameThrough()
    {
        Assert.Equal(
            @"cd 'C:\Code\sky'; claude --name 'nightly triage' --remote-control",
            Commands.NewSessionLine(@"C:\Code\sky", "nightly triage"));
    }

    // Single quotes are how PowerShell is told to take a path literally, so a folder or a
    // name containing one has to double it or the rest of the line becomes code.
    [Fact]
    public void QuotesAFolderOrNameThatContainsAQuote()
    {
        Assert.Equal(
            @"cd 'C:\Code\kk''s repo'; claude --name 'kk''s session' --remote-control",
            Commands.NewSessionLine(@"C:\Code\kk's repo", "kk's session"));
    }

    /// <summary>
    /// A session this app starts is one in a terminal nobody is watching, which is precisely
    /// the session that wants answering from somewhere else. Remote Control can only be
    /// asked for at launch or from inside the session, so a line that leaves it off is a
    /// session that can be seen on a phone and never typed at.
    /// </summary>
    [Fact]
    public void EveryNewSessionIsReachableFromElsewhere()
    {
        Assert.EndsWith("--remote-control", Commands.NewSessionLine(@"C:\Code\sky", null));
        Assert.EndsWith("--remote-control", Commands.NewSessionLine(@"C:\Code\sky", "nightly triage"));
    }

    [Fact]
    public void TreatsAnEmptyNameAsNoName()
    {
        Assert.Equal(@"cd 'C:\Code\sky'; claude --remote-control", Commands.NewSessionLine(@"C:\Code\sky", ""));
    }

    [Fact]
    public void RefusesAFolderThatIsNotThere()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sky-session-no-such-folder-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<UsageException>(() => Commands.New(Parse("--in", missing, "--dry-run")));
    }

    // `new abc123` is someone reaching for `resume`; a verb that starts a session cannot
    // act on the id they typed, so it must say so rather than open an unrelated terminal.
    [Fact]
    public void RefusesABareArgument()
    {
        Assert.Throws<UsageException>(() => Commands.New(Parse("abc123", "--dry-run")));
    }

    [Fact]
    public void RefusesAFlagItDoesNotKnow()
    {
        Assert.Throws<UsageException>(() => Commands.New(Parse("--prompt", "hello", "--dry-run")));
    }
}
