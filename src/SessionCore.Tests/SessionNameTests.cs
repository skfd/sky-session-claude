using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks what a session is called when the app relaunches it. The failure this guards
/// against is quiet: a session comes back under a name that changed for no reason, and the
/// operator cannot find on their phone the thing they were just working on.
/// </summary>
public class SessionNameTests
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";
    private const string Cwd = @"C:\Users\kk\Code\ontario-address-changes";

    private static LiveSession Live(string? name, string? nameSource) => new()
    {
        SessionId = Id,
        Cwd = Cwd,
        Name = name,
        NameSource = nameSource,
    };

    // --- whose name is it -----------------------------------------------------

    /// <summary>
    /// The registry writes nameSource only for names the CLI invented. A name that came
    /// from --name or --remote-control is stored with the field omitted, so the absence of
    /// a source is the whole signal — an earlier version of this looked for "custom", a
    /// value the CLI never writes, and so never carried a chosen name over at all.
    /// </summary>
    [Fact]
    public void AbsentSourceMeansTheOperatorChoseIt() =>
        Assert.True(SessionName.IsChosen(Live("night shift", nameSource: null)));

    [Theory]
    [InlineData("derived")]
    [InlineData("collision")]
    public void AnInventedNameIsNotChosen(string source) =>
        Assert.False(SessionName.IsChosen(Live("ontario-address-changes-6c", source)));

    /// <summary>No name at all is nothing to preserve, whatever the source field says.</summary>
    [Fact]
    public void NoNameIsNotChosen() =>
        Assert.False(SessionName.IsChosen(Live(name: null, nameSource: null)));

    // --- what we call it ------------------------------------------------------

    [Fact]
    public void ATitledSessionIsCalledByItsTitle() =>
        Assert.Equal("Add retry logic to address-vault download",
            SessionName.For(Id, Cwd, "Add retry logic to address-vault download"));

    /// <summary>
    /// A terminal opened and never used has no title to take a name from, and the folder is
    /// honestly all there is to say about it.
    /// </summary>
    [Fact]
    public void AnUntitledSessionFallsBackToItsFolderAndId() =>
        Assert.Equal("ontario-address-changes-b9", SessionName.For(Id, Cwd, title: null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTitleIsNoTitle(string title) =>
        Assert.Equal("ontario-address-changes-b9", SessionName.For(Id, Cwd, title));

    /// <summary>
    /// The point of the id prefix over the CLI's own suffix: it is the same two characters
    /// after every restart, and it is a handle every verb already accepts.
    /// </summary>
    [Fact]
    public void TheFallbackIsTheSameAfterEveryRestart() =>
        Assert.Equal(SessionName.For(Id, Cwd, null), SessionName.For(Id, Cwd, null));

    [Fact]
    public void TwoSessionsInOneFolderGetDifferentNames() =>
        Assert.NotEqual(
            SessionName.For(Id, Cwd, null),
            SessionName.For("1cb5c216-0821-4d58-87cf-45af838e3698", Cwd, null));

    [Fact]
    public void AFolderWithPunctuationIsSlugged() =>
        Assert.Equal("skyfallsdown-com-b9", SessionName.For(Id, @"C:\Users\kk\Code\skyfallsdown.com", null));

    [Fact]
    public void ATrailingSeparatorIsNotTheFolderName() =>
        Assert.Equal("demo-b9", SessionName.For(Id, @"C:\Users\kk\Code\demo\", null));

    /// <summary>Nothing to go on at all still has to produce a usable name.</summary>
    [Fact]
    public void NoFolderStillNamesTheSession() =>
        Assert.Equal("session-b9", SessionName.For(Id, cwd: null, title: null));

    // --- length ---------------------------------------------------------------

    [Fact]
    public void ATitleThatFitsIsLeftAlone()
    {
        var title = new string('a', SessionName.MaxLength);
        Assert.Equal(title, SessionName.For(Id, Cwd, title));
    }

    /// <summary>A name is one line on a phone; a long title is cut, and cut between words.</summary>
    [Fact]
    public void AnOverlongTitleIsCutOnAWordBoundary()
    {
        var name = SessionName.For(Id, Cwd,
            "Investigate the address importer timing out on the Brant and Guelph mass change events");

        Assert.True(name.Length <= SessionName.MaxLength);
        Assert.DoesNotContain("  ", name);
        Assert.EndsWith("Brant and", name);   // cut between words, not through one
    }

    /// <summary>One unbroken word has no boundary to cut on, and still must not run long.</summary>
    [Fact]
    public void AnOverlongWordIsCutAnyway() =>
        Assert.Equal(SessionName.MaxLength, SessionName.For(Id, Cwd, new string('x', 200)).Length);

    [Fact]
    public void ATitleSpreadOverLinesBecomesOneLine() =>
        Assert.Equal("fix the importer", SessionName.For(Id, Cwd, "  fix\r\n  the   importer  "));

    // --- which folder ----------------------------------------------------------

    [Fact]
    public void TheRepoIsTheFolderASessionRanIn() =>
        Assert.Equal("ontario-address-changes", SessionName.RepoOf(Cwd));

    /// <summary>
    /// Three sessions under one repo's worktrees are all working on that repo. Named after
    /// their branches they would look like three projects, and what actually tells them
    /// apart — their subjects — would have nothing to sit next to.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\kk\Code\sky-session-claude\.claude\worktrees\force-resume")]
    [InlineData(@"C:\Users\kk\Code\sky-session-claude\.claude\worktrees\force-resume\src")]
    [InlineData("C:/Users/kk/Code/sky-session-claude/.claude/worktrees/force-resume")]
    public void AWorktreeIsNamedAfterItsRepo(string cwd) =>
        Assert.Equal("sky-session-claude", SessionName.RepoOf(cwd));

    [Fact]
    public void ATrailingSeparatorIsNotTheRepoName() =>
        Assert.Equal("demo", SessionName.RepoOf(@"C:\Users\kk\Code\demo\"));

    [Fact]
    public void NoFolderIsNoRepo() =>
        Assert.Equal("", SessionName.RepoOf(null));

    // --- subject and folder -----------------------------------------------------

    [Fact]
    public void ANameIsItsSubjectThenItsRepo() =>
        Assert.Equal("Basemap treatments in Chrome — ontario-address-changes",
            SessionName.Compose("Basemap treatments in Chrome", Cwd));

    /// <summary>
    /// House style is sentence case, which the models write already — so this only fixes the
    /// first letter, and leaves "Chrome" and "OSM" as it found them.
    /// </summary>
    [Fact]
    public void ASubjectIsSentenceCased() =>
        Assert.Equal("Basemap treatments in Chrome — ontario-address-changes",
            SessionName.Compose("basemap treatments in Chrome", Cwd));

    /// <summary>
    /// A composed name comes back through here on the next restart. Growing a second folder
    /// each time would be the length limit eating the subject, one restart at a time.
    /// </summary>
    [Fact]
    public void ComposingAComposedNameDoesNotRepeatTheRepo() =>
        Assert.Equal("Basemap treatments — ontario-address-changes",
            SessionName.Compose("Basemap treatments — ontario-address-changes", Cwd));

    [Fact]
    public void AComposedNameFitsOnOneLine()
    {
        var name = SessionName.Compose(
            "Investigate the address importer timing out on the Brant mass change events", Cwd);

        Assert.True(name.Length <= SessionName.MaxLength, name);
        Assert.EndsWith(" — ontario-address-changes", name);
    }

    /// <summary>
    /// Past a point the folder is what has to go: a subject cut to five characters says less
    /// than the subject alone, and the subject is the half that identifies the session.
    /// </summary>
    [Fact]
    public void AVeryLongRepoLosesTheFolderRatherThanTheSubject()
    {
        var name = SessionName.Compose("Basemap treatments in Chrome",
            @"C:\Users\kk\Code\" + new string('r', 50));

        Assert.Equal("Basemap treatments in Chrome", name);
    }

    [Fact]
    public void NoSubjectComposesToNoName() =>
        Assert.Equal("", SessionName.Compose("   ", Cwd));

    // --- the floor --------------------------------------------------------------

    [Fact]
    public void TheFloorIsTheRepoAndTheIdPrefix() =>
        Assert.Equal("ontario-address-changes-b9", SessionName.Floor(Id, Cwd));

    [Fact]
    public void TheFloorFollowsTheRepoOutOfAWorktree() =>
        Assert.Equal("sky-session-claude-b9",
            SessionName.Floor(Id, @"C:\Users\kk\Code\sky-session-claude\.claude\worktrees\force-resume"));

    [Fact]
    public void NothingToGoOnStillNamesTheSession() =>
        Assert.Equal("session-b9", SessionName.Floor(Id, cwd: null));

    // --- recognising a placeholder ----------------------------------------------

    /// <summary>
    /// Naming writes into the transcript, so a floor comes back as a custom-title. Read as a
    /// title it would be composed into a name and written again — a placeholder made
    /// permanent by being used once.
    /// </summary>
    [Theory]
    [InlineData("ontario-address-changes-b9")]   // ours
    [InlineData("ontario-address-changes-6c")]   // the CLI's, suffix drawn fresh each launch
    [InlineData("ontario-address-changes")]      // no suffix at all
    public void APlaceholderIsRecognisedByItsShape(string name) =>
        Assert.True(SessionName.IsFloor(name, Id, Cwd));

    [Theory]
    [InlineData("night shift")]
    [InlineData("Add retry logic to address-vault download")]
    [InlineData("ontario-address-changes-6cd")]
    [InlineData("some-other-repo-6c")]
    [InlineData("")]
    [InlineData(null)]
    public void ARealNameIsNotAPlaceholder(string? name) =>
        Assert.False(SessionName.IsFloor(name, Id, Cwd));

    /// <summary>
    /// The documented misfire: a name genuinely typed that happens to read like "repo-XX" is
    /// taken for a placeholder. This is why provenance is primary and the shape check is only
    /// the fallback for history written before the store existed.
    /// </summary>
    [Fact]
    public void ThisCheckCannotTellATypedNameOfThatShapeApart() =>
        Assert.True(SessionName.IsFloor("ontario-address-changes-v2", Id, Cwd));

    // --- quoting --------------------------------------------------------------

    [Fact]
    public void AQuoteIsDoubledForTheShell() =>
        Assert.Equal("'it''s working'", SessionName.Quote("it's working"));
}
