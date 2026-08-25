using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// `standby` answers a question none of the other verbs ask: not "which session", but "which
/// folders should have one up at all", because from a phone a project with nothing running is
/// a project that is not there. What can go wrong is the list it produces — a folder that has
/// been deleted, a worktree an agent made and will delete, the same repo twice, or a repo
/// already answering the phone getting a second identical row.
/// </summary>
public class StandbyTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 18, 0, 0);

    private static SessionInfo In(string cwd, double daysAgo) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        Cwd = cwd,
        LastActive = Now.AddDays(-daysAgo),
    };

    /// <summary>Every folder in these tests is pretended to exist unless a test says otherwise.</summary>
    private static StandbyPlan Decide(
        IEnumerable<SessionInfo> sessions,
        IEnumerable<LiveSession>? live = null,
        TimeSpan? window = null,
        int max = int.MaxValue,
        Func<string, bool>? folderExists = null,
        Func<string, bool>? isRepo = null) =>
        Standby.Decide(sessions, live ?? [], Now, window, max,
            folderExists ?? (_ => true), isRepo ?? (_ => true));

    private static LiveSession Running(string cwd, string name, bool remoteControl) =>
        LiveSessionRegistry.Parse(
            $$"""
            {"pid":4242,"sessionId":"b9e83ad3-8742-4f86-b5e3-40e844f24da1",
             "cwd":"{{cwd.Replace(@"\", @"\\")}}","version":"2.1.241","kind":"interactive",
             "entrypoint":"cli","name":"{{name}}"
             {{(remoteControl ? ",\"bridgeSessionId\":\"session_01NpwuF1HVr5CRthp5YS8SWH\"" : "")}}}
            """)!;

    [Fact]
    public void TakesTheFoldersWorkedInInsideTheWindow()
    {
        var plan = Decide([In(@"C:\Code\sky", 1), In(@"C:\Code\vault", 30)], window: TimeSpan.FromDays(7));

        Assert.Equal([@"C:\Code\sky"], plan.Open.Select(t => t.Folder));
        Assert.Empty(plan.Skipped);
    }

    /// <summary>
    /// A folder that fell outside the window is not a skip. Skips are things the operator
    /// might want to act on; "I have not touched that repo in a month" is not one of them,
    /// and every repo they have ever opened would drown the ones they just left.
    /// </summary>
    [Fact]
    public void DoesNotReportTheFoldersTheWindowLeftBehind()
    {
        var plan = Decide([In(@"C:\Code\vault", 30)], window: TimeSpan.FromDays(7));

        Assert.Empty(plan.Open);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void ThreeSessionsInOneRepoAreOneProject()
    {
        var plan = Decide([In(@"C:\Code\sky", 3), In(@"C:\Code\sky", 1), In(@"C:\Code\sky", 2)]);

        var only = Assert.Single(plan.Open);
        Assert.Equal(@"C:\Code\sky", only.Folder);
        Assert.Equal("sky", only.Project);

        // The newest turn in the folder, not the one that happened to be scanned first.
        Assert.Equal(Now.AddDays(-1), only.LastActive);
    }

    [Fact]
    public void OrdersTheNewestProjectFirst()
    {
        var plan = Decide([In(@"C:\Code\old", 5), In(@"C:\Code\new", 1), In(@"C:\Code\mid", 3)]);

        Assert.Equal(["new", "mid", "old"], plan.Open.Select(t => t.Project));
    }

    /// <summary>
    /// The one thing standby must not do. Two sessions on standby in one repo are two
    /// identical rows on a phone, in a list that shows no folders to tell them apart.
    /// </summary>
    [Fact]
    public void SkipsARepoThatIsAlreadyAnsweringThePhone()
    {
        var plan = Decide(
            [In(@"C:\Code\sky", 1)],
            [Running(@"C:\Code\sky", "sky-6c", remoteControl: true)]);

        Assert.Empty(plan.Open);
        Assert.Contains("already on standby", Assert.Single(plan.Skipped).Reason);
        Assert.Contains("sky-6c", plan.Skipped[0].Reason);
    }

    /// <summary>
    /// A session at the desk is not reachable from anywhere. Remote Control is the bridge,
    /// and a busy terminal without one is exactly the case standby exists to fix.
    /// </summary>
    [Fact]
    public void ASessionWithoutRemoteControlDoesNotCountAsReachable()
    {
        var plan = Decide(
            [In(@"C:\Code\sky", 1)],
            [Running(@"C:\Code\sky", "sky-6c", remoteControl: false)]);

        Assert.Single(plan.Open);
        Assert.Empty(plan.Skipped);
    }

    /// <summary>The registry and the transcripts do not have to agree on how to spell a path.</summary>
    [Fact]
    public void MatchesAReachableFolderThroughSlashesAndCase()
    {
        var plan = Decide(
            [In(@"C:\Code\Sky", 1)],
            [Running(@"c:/code/sky/", "sky-6c", remoteControl: true)]);

        Assert.Empty(plan.Open);
        Assert.Single(plan.Skipped);
    }

    /// <summary>
    /// Worktrees are the reason this filter exists: an agent makes one per task under the
    /// repo's .claude, works there for an hour and deletes it. They are the newest folders
    /// on disk at exactly the moment they stop being folders at all.
    /// </summary>
    [Fact]
    public void IgnoresTheWorktreesAnAgentMade()
    {
        var plan = Decide([
            In(@"C:\Code\sky", 2),
            In(@"C:\Code\sky\.claude\worktrees\session-links", 1),
        ]);

        Assert.Equal([@"C:\Code\sky"], plan.Open.Select(t => t.Folder));
        Assert.Empty(plan.Skipped);
    }

    /// <summary>
    /// A deleted repo keeps its transcripts, and `cd` into a folder that is gone leaves the
    /// shell wherever it started — so the session would come up on a phone claiming to be
    /// somewhere it is not. That is worth a line in the report, not a silent drop.
    /// </summary>
    [Fact]
    public void ReportsAFolderThatIsNoLongerThere()
    {
        var plan = Decide(
            [In(@"C:\Code\gone", 1)],
            folderExists: folder => folder != @"C:\Code\gone");

        Assert.Empty(plan.Open);
        Assert.Equal("the folder is no longer there", Assert.Single(plan.Skipped).Reason);
    }

    /// <summary>
    /// A home folder and the folder every repo sits under look exactly like projects in a
    /// transcript — same recency, more sessions than most — and they cost a row each on a
    /// screen that is nothing but rows.
    /// </summary>
    [Fact]
    public void ReportsAFolderThatIsNotARepo()
    {
        var plan = Decide(
            [In(@"C:\Users\kk", 1), In(@"C:\Code\sky", 1)],
            isRepo: folder => folder == @"C:\Code\sky");

        Assert.Equal([@"C:\Code\sky"], plan.Open.Select(t => t.Folder));
        Assert.Contains("no .git", Assert.Single(plan.Skipped).Reason);
    }

    /// <summary>
    /// A deleted repo is gone, not un-versioned. Reporting the second reason for the first
    /// case sends you looking for a folder that is not there.
    /// </summary>
    [Fact]
    public void AMissingFolderIsReportedMissingRatherThanUnversioned()
    {
        var plan = Decide([In(@"C:\Code\gone", 1)], folderExists: _ => false, isRepo: _ => false);

        Assert.Equal("the folder is no longer there", Assert.Single(plan.Skipped).Reason);
    }

    /// <summary>A worktree and a submodule write a .git file rather than a folder.</summary>
    [Fact]
    public void AGitFileCountsAsMuchAsAGitFolder()
    {
        var root = Directory.CreateTempSubdirectory("sky-standby-").FullName;
        try
        {
            var asFolder = Directory.CreateDirectory(Path.Combine(root, "folder"));
            Directory.CreateDirectory(Path.Combine(asFolder.FullName, ".git"));

            var asFile = Directory.CreateDirectory(Path.Combine(root, "file"));
            File.WriteAllText(Path.Combine(asFile.FullName, ".git"), "gitdir: ../real/.git");

            var neither = Directory.CreateDirectory(Path.Combine(root, "neither"));

            Assert.True(Standby.HasGit(asFolder.FullName));
            Assert.True(Standby.HasGit(asFile.FullName));
            Assert.False(Standby.HasGit(neither.FullName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionsWithNoRecordedFolderAreNotProjects()
    {
        var plan = Decide([
            new SessionInfo { SessionId = "a", Cwd = SessionInfo.UnknownCwd, LastActive = Now },
            new SessionInfo { SessionId = "b", Cwd = null, LastActive = Now },
        ]);

        Assert.Empty(plan.Open);
        Assert.Empty(plan.Skipped);
    }

    /// <summary>
    /// The cap keeps the newest, and says what it dropped. A sweep that opens terminals must
    /// never leave the caller thinking it covered everything it found.
    /// </summary>
    [Fact]
    public void TheCapKeepsTheNewestAndReportsTheRest()
    {
        var plan = Decide([In(@"C:\Code\a", 1), In(@"C:\Code\b", 2), In(@"C:\Code\c", 3)], max: 2);

        Assert.Equal(["a", "b"], plan.Open.Select(t => t.Project));
        Assert.Equal("c", Assert.Single(plan.Skipped).Project);
        Assert.Contains("--recent 2", plan.Skipped[0].Reason);
    }

    /// <summary>
    /// A project already on standby is reported, not opened, so it should not eat one of the
    /// slots — otherwise `--recent 3` with one repo already up opens two.
    /// </summary>
    [Fact]
    public void AReachableProjectDoesNotSpendOneOfTheSlots()
    {
        var plan = Decide(
            [In(@"C:\Code\a", 1), In(@"C:\Code\b", 2), In(@"C:\Code\c", 3)],
            [Running(@"C:\Code\a", "a-6c", remoteControl: true)],
            max: 2);

        Assert.Equal(["b", "c"], plan.Open.Select(t => t.Project));
        Assert.Equal("a", Assert.Single(plan.Skipped).Project);
    }
}
