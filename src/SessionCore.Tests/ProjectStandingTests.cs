using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Project standings: the operator's word on whether a folder is still worked on. The
/// overlapping-writers machinery is shared with the other sidecars and pinned next door; this
/// holds the file format, the batch write, name resolution, and what standby does with it.
/// </summary>
public class ProjectStandingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stand-{Guid.NewGuid():N}");

    public ProjectStandingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ProjectStanding S(Standing s, string? note = null) =>
        new() { Standing = s, Note = note, At = DateTimeOffset.Now };

    [Fact]
    public void AbsentMeansActive()
    {
        var store = new ProjectStandingStore(_dir);
        Assert.Equal(Standing.Active, store.Of(@"C:\Code\anything"));
        Assert.Null(store.Get(@"C:\Code\anything"));
    }

    [Fact]
    public void RoundTripsABatchUnderOneWrite()
    {
        new ProjectStandingStore(_dir).Set(
            [@"C:\Code\experiment-1", @"C:\Code\experiment-2\"], S(Standing.Parked, "runner paused"));

        var read = new ProjectStandingStore(_dir);
        Assert.Equal(2, read.All.Count);
        Assert.Equal(Standing.Parked, read.Of(@"C:\Code\experiment-2"));
        Assert.Equal("runner paused", read.Get("C:/Code/experiment-1")!.Note);
    }

    [Fact]
    public void ActiveRemovesTheEntryRatherThanStoringIt()
    {
        var store = new ProjectStandingStore(_dir);
        store.Set(@"C:\Code\a", S(Standing.Done));
        store.Set(@"C:\Code\a", S(Standing.Active));

        Assert.Empty(new ProjectStandingStore(_dir).All);
        Assert.DoesNotContain("active", File.ReadAllText(Path.Combine(_dir, "standings.json")));
    }

    [Fact]
    public void AnUnknownWordOnDiskReadsAsActive()
    {
        File.WriteAllText(Path.Combine(_dir, "standings.json"),
            """{ "C:\\Code\\a": { "standing": "hibernating" }, "C:\\Code\\b": { "standing": "automated" } }""");

        var store = new ProjectStandingStore(_dir);
        Assert.Equal(Standing.Active, store.Of(@"C:\Code\a"));
        Assert.Equal(Standing.Automated, store.Of(@"C:\Code\b"));
    }

    [Theory]
    [InlineData("parked", Standing.Parked)]
    [InlineData("Done", Standing.Done)]
    [InlineData("auto", Standing.Automated)]
    [InlineData(" active ", Standing.Active)]
    public void ReadsTheWords(string word, Standing expected) =>
        Assert.Equal(expected, ProjectStanding.FromWire(word));

    [Fact]
    public void ATypoIsNotActive() => Assert.Null(ProjectStanding.FromWire("parkd"));

    private static readonly string[] Pool =
    [
        @"C:\Code\experiment-2026-08-26",
        @"C:\Code\experiment-runner",
        @"C:\Code\skfd",
        @"C:\Code\skfd-profile",
        @"D:\Work\skfd",
    ];

    [Fact]
    public void AGlobMatchesLeafNamesOnly()
    {
        var r = StandingTargets.Resolve(["experiment-*"], Pool, _ => false);
        Assert.Equal([@"C:\Code\experiment-2026-08-26", @"C:\Code\experiment-runner"], r.Folders);
        Assert.Empty(r.Unmatched);
    }

    [Fact]
    public void ABareNameIsExactNotAPrefix_AndMeansEveryFolderOfThatName()
    {
        var r = StandingTargets.Resolve(["SKFD"], Pool, _ => false);
        Assert.Equal([@"C:\Code\skfd", @"D:\Work\skfd"], r.Folders);
    }

    [Fact]
    public void APathNamesItsFolderOutright()
    {
        var r = StandingTargets.Resolve([@"D:\Work\skfd\"], Pool, p => p == @"D:\Work\skfd");
        Assert.Equal([@"D:\Work\skfd"], r.Folders);
    }

    [Fact]
    public void WhatMatchesNothingIsReported_AndOverlapsCountOnce()
    {
        var r = StandingTargets.Resolve(["skfd*", "skfd-profile", "nope-*"], Pool, _ => false);
        Assert.Equal(3, r.Folders.Count);
        Assert.Equal(["nope-*"], r.Unmatched);
    }

    [Fact]
    public void StandbySkipsASetAsideProject_AndSaysWhy()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0);
        SessionInfo At(string cwd) => new()
        {
            SessionId = Guid.NewGuid().ToString(),
            FilePath = "x.jsonl",
            Cwd = cwd,
            LastActive = now.AddHours(-1),
            Status = SessionStatus.Complete,
        };

        var plan = Standby.Decide(
            [At(@"C:\Code\live-one"), At(@"C:\Code\parked-one")],
            now,
            folderExists: _ => true,
            isRepo: _ => true,
            hostFor: _ => null,
            isTrusted: _ => true,
            standingOf: f => f.EndsWith("parked-one") ? Standing.Parked : Standing.Active);

        Assert.Equal(["live-one"], plan.Open.Select(o => o.Project));
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal("parked-one", skip.Project);
        Assert.StartsWith("parked", skip.Reason);
    }
}
