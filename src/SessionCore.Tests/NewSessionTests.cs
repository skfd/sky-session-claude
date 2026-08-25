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
        Assert.Equal(@"cd 'C:\Code\sky'; claude", Commands.NewSessionLine(@"C:\Code\sky", null));
    }

    [Fact]
    public void PassesAChosenNameThrough()
    {
        Assert.Equal(
            @"cd 'C:\Code\sky'; claude --name 'nightly triage'",
            Commands.NewSessionLine(@"C:\Code\sky", "nightly triage"));
    }

    // Single quotes are how PowerShell is told to take a path literally, so a folder or a
    // name containing one has to double it or the rest of the line becomes code.
    [Fact]
    public void QuotesAFolderOrNameThatContainsAQuote()
    {
        Assert.Equal(
            @"cd 'C:\Code\kk''s repo'; claude --name 'kk''s session'",
            Commands.NewSessionLine(@"C:\Code\kk's repo", "kk's session"));
    }

    [Fact]
    public void TreatsAnEmptyNameAsNoName()
    {
        Assert.Equal(@"cd 'C:\Code\sky'; claude", Commands.NewSessionLine(@"C:\Code\sky", ""));
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

    // The name goes in under --name even when Remote Control is asked for, because only
    // --name reaches the registry — the same thing RestartPolicy.ResumeCommand knows.
    [Fact]
    public void AsksForRemoteControlWithoutHangingTheNameOnIt()
    {
        Assert.Equal(
            @"cd 'C:\Code\sky'; claude --remote-control",
            Commands.NewSessionLine(@"C:\Code\sky", null, remoteControl: true));

        Assert.Equal(
            @"cd 'C:\Code\sky'; claude --name 'night shift' --remote-control",
            Commands.NewSessionLine(@"C:\Code\sky", "night shift", remoteControl: true));
    }
}
