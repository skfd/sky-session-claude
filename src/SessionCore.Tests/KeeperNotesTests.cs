using SessionCore;

namespace SessionCore.Tests;

public class KeeperNotesTests
{
    private static StandbySkip Held(string project, string reason) =>
        new() { Folder = $@"C:\Code\{project}", Project = project, Reason = reason };

    private const string NoGit = "no .git — a folder you asked something in, not a project";
    private const string Untrusted = "never trusted with Claude Code — the Standby button records the trust and opens it";

    /// <summary>The 2026-11-09 log: the same two reasons, every minute, filling it in a day.</summary>
    [Fact]
    public void AStandingReasonIsWrittenOnce()
    {
        var notes = new KeeperNotes();
        Assert.Equal(2, notes.Lines([Held("Code", NoGit), Held("thunder-bay", Untrusted)]).Count);
        for (var i = 0; i < 10; i++)
            Assert.Empty(notes.Lines([Held("Code", NoGit), Held("thunder-bay", Untrusted)]));
    }

    /// <summary>What the doctor reads to call a missing host a warning, not a failure.</summary>
    [Fact]
    public void ABackOffIsWrittenEveryLookBecauseItCountsDown()
    {
        var notes = new KeeperNotes();
        Assert.Single(notes.Lines([Held("hamilton", "its last host went down — trying again in 16 min")]));
        Assert.Single(notes.Lines([Held("hamilton", "its last host went down — trying again in 15 min")]));
    }

    [Fact]
    public void AReasonThatChangesIsWritten()
    {
        var notes = new KeeperNotes();
        notes.Lines([Held("x", Untrusted)]);
        var lines = notes.Lines([Held("x", "its host has only just gone — looking again in a minute")]);
        Assert.Equal(["keeper: x held — its host has only just gone — looking again in a minute"], lines);
    }

    [Fact]
    public void AProjectHeldAgainAfterAGapIsWrittenAgain()
    {
        var notes = new KeeperNotes();
        notes.Lines([Held("x", Untrusted)]);
        notes.Lines([]);
        Assert.Single(notes.Lines([Held("x", Untrusted)]));
    }
}
