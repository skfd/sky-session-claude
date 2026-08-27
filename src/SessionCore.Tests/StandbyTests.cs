using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// `standby` answers a question none of the other verbs ask: not "which session", but "which
/// folders should have one up at all", because from a phone a project with nothing running is
/// a project that is not there. What can go wrong is the list it produces — a folder that has
/// been deleted, a worktree an agent made and will delete, the same repo twice, or a repo a
/// host is already serving getting a second identical row.
///
/// A live host is the only thing that means "already on standby", and the tests below hold
/// that line from both sides: a host stops a folder, and nothing else does. It was once any
/// session with Remote Control connected, which read as reasonable and was wrong — a folder
/// held by a bridged `claude --remote-control` terminal is reachable from a phone and still
/// offers no way to start a second conversation there, so passing it over left the repo in
/// exactly the state standby exists to fix.
/// </summary>
public class StandbyTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 18, 0, 0);

    private static SessionInfo In(string cwd, double daysAgo) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        Cwd = cwd,
        LastActive = Now.AddDays(-daysAgo),

        // The transcript folder is where a host writes its pointer, and standby reads it off
        // the scan rather than slugging the path. Named after the repo here only so a test
        // can tell one from another.
        FilePath = $@"C:\projects\{ProjectOf(cwd)}\{Guid.NewGuid():N}.jsonl",
    };

    private static string ProjectOf(string cwd) => Standby.ProjectOf(cwd);

    private static BridgePointer Host(int pid = 4242) =>
        new() { SessionId = "session_01WH7wucUtkLYNU3bM3HM38d", Pid = pid };

    /// <summary>Every folder in these tests is pretended to exist unless a test says otherwise.</summary>
    private static StandbyPlan Decide(
        IEnumerable<SessionInfo> sessions,
        TimeSpan? window = null,
        int max = int.MaxValue,
        Func<string, bool>? folderExists = null,
        Func<string, bool>? isRepo = null,
        Func<string, BridgePointer?>? hostFor = null) =>
        Standby.Decide(sessions, Now, window, max,
            folderExists ?? (_ => true), isRepo ?? (_ => true), hostFor ?? (_ => null));

    /// <summary>A host serving the transcript folder of the named project, and nothing else.</summary>
    private static Func<string, BridgePointer?> HostServing(string project, int pid = 4242) =>
        dir => ProjectOf(dir).Equals(project, StringComparison.OrdinalIgnoreCase) ? Host(pid) : null;

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
    /// The one thing standby must not do, and the mistake it is most likely to make, because
    /// it is the one it makes to its own output: a `claude rc` host publishes no session of its
    /// own, so a folder it is serving looks empty in the registry whenever no conversation
    /// happens to be open. Two hosts in one repo are two identical rows on a phone, in a list
    /// that shows no folders to tell them apart.
    /// </summary>
    [Fact]
    public void SkipsARepoAHostIsAlreadyServing()
    {
        var plan = Decide([In(@"C:\Code\sky", 1)], hostFor: HostServing("sky", pid: 6328));

        Assert.Empty(plan.Open);
        Assert.Contains("already on standby", Assert.Single(plan.Skipped).Reason);
        Assert.Contains("claude rc host (pid 6328)", plan.Skipped[0].Reason);
    }

    /// <summary>
    /// The other half of that rule, and the one this used to get wrong. With no host serving
    /// it a folder opens, whatever else happens to be running there — which is now a fact
    /// about the signature as much as about this assertion, since `Decide` is no longer handed
    /// the registry and so cannot pass a folder over for anything it finds in one.
    ///
    /// That is the point. What it used to find was a bridged `claude --remote-control`
    /// terminal, which is reachable from a phone and offers exactly one conversation there
    /// with no way to open a second — the state standby exists to get you out of, read as a
    /// reason not to. A host opens beside it now, and since the terminal is not in the way,
    /// the plan does not mention it either.
    /// </summary>
    [Fact]
    public void OnlyAHostStopsAFolderFromOpening()
    {
        var plan = Decide([In(@"C:\Code\sky", 1)], hostFor: HostServing("somewhere-else"));

        Assert.Equal([@"C:\Code\sky"], plan.Open.Select(t => t.Folder));
        Assert.Empty(plan.Skipped);
    }

    /// <summary>
    /// The pointer is asked of the transcript folder, not the repo — that is where the host
    /// writes it, and reading it off the scan is what keeps Claude Code's slug rule out of
    /// this code.
    /// </summary>
    [Fact]
    public void AsksAboutTheTranscriptFolderRatherThanTheRepo()
    {
        string? asked = null;
        Decide([In(@"C:\Code\sky", 1)], hostFor: dir => { asked = dir; return null; });

        Assert.Equal(@"C:\projects\sky", asked);
    }

    /// <summary>
    /// Two sessions in one repo do not have to agree on how to spell its path — a `cd` with a
    /// trailing slash, a drive letter typed in either case. Spelled two ways they would be two
    /// projects, which is two rows on the phone and two hosts in one folder.
    /// </summary>
    [Fact]
    public void MatchesOneFolderThroughSlashesAndCase()
    {
        var plan = Decide([In(@"C:\Code\Sky", 2), In(@"c:/code/sky/", 1)]);

        var only = Assert.Single(plan.Open);
        Assert.Equal(Now.AddDays(-1), only.LastActive);
        Assert.Empty(plan.Skipped);
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
    /// slots — otherwise `--recent 2` with one repo already served opens one.
    /// </summary>
    [Fact]
    public void AServedProjectDoesNotSpendOneOfTheSlots()
    {
        var plan = Decide(
            [In(@"C:\Code\a", 1), In(@"C:\Code\b", 2), In(@"C:\Code\c", 3)],
            max: 2,
            hostFor: HostServing("a"));

        Assert.Equal(["b", "c"], plan.Open.Select(t => t.Project));
        Assert.Equal("a", Assert.Single(plan.Skipped).Project);
    }
}
