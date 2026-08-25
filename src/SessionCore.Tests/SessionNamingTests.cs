using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The seam between deciding a name and writing one, and the invariant it exists for: every
/// name Sky writes is recorded in the same operation. A write that skips the record leaves a
/// name indistinguishable from one the operator typed, and therefore frozen forever.
/// </summary>
public class SessionNamingTests : IDisposable
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";
    private const string Cwd = @"C:\Users\kk\Code\vagabond-map";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"naming-{Guid.NewGuid():N}");
    private readonly NameStore _store;

    public SessionNamingTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new NameStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static NameInputs Inputs(string? aiTitle = null, string? customTitle = null, LiveSession? live = null) => new()
    {
        SessionId = Id,
        Cwd = Cwd,
        AiTitle = aiTitle,
        CustomTitle = customTitle,
        Live = live,
        HasContent = aiTitle is not null || customTitle is not null,
    };

    // --- the invariant --------------------------------------------------------

    [Fact]
    public void ALaunchRecordsTheNameItChose()
    {
        var name = SessionNaming.NameForLaunch(Inputs(aiTitle: "Basemap treatments in Chrome"), _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", name);
        Assert.Equal(NameOrigin.Title, new NameStore(_dir).OriginOf(Id, name));
    }

    /// <summary>
    /// And the point of recording it: next time round the name is recognised as Sky's rather
    /// than as one the operator typed, so it can still be improved on — and is not, because
    /// nothing better exists yet.
    /// </summary>
    [Fact]
    public void ASecondLaunchKeepsWhatTheFirstChose()
    {
        var first = SessionNaming.NameForLaunch(Inputs(aiTitle: "Basemap treatments in Chrome"), _store);
        var second = SessionNaming.NameForLaunch(
            Inputs(aiTitle: "Basemap treatments in Chrome", customTitle: first), _store);

        Assert.Equal(first, second);
    }

    /// <summary>A dry run promises to change nothing, and names.json is something.</summary>
    [Fact]
    public void PlanningRecordsNothing()
    {
        var planned = SessionNaming.PlanLaunch(Inputs(aiTitle: "Basemap treatments in Chrome"), _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", planned.Name);
        Assert.Null(new NameStore(_dir).Get(Id));
    }

    // --- always a name --------------------------------------------------------

    /// <summary>
    /// Unlike the policy, a launch always produces one: leaving --name off would let the CLI
    /// re-derive a name with a fresh suffix, which is the churn this exists to stop.
    /// </summary>
    [Fact]
    public void ASessionWithNothingToSayStillGetsANameToLaunchUnder() =>
        Assert.Equal("vagabond-map-b9", SessionNaming.PlanLaunch(Inputs(), _store).Name);

    /// <summary>
    /// A name of the operator's is carried over untouched — and not re-recorded as Sky's,
    /// which would hand Sky permission to replace it on the next pass.
    /// </summary>
    [Fact]
    public void ANameYouChoseIsCarriedOverAndNotClaimed()
    {
        var name = SessionNaming.NameForLaunch(Inputs(customTitle: "night shift"), _store);

        Assert.Equal("night shift", name);
        Assert.Null(new NameStore(_dir).Get(Id));
    }

    // --- what the policy is shown ---------------------------------------------

    /// <summary>
    /// A session that moved folders mid-conversation has two answers for where it ran. The
    /// registry's is where it is *now*, and that is the one a name should point at.
    /// </summary>
    [Fact]
    public void TheLiveFolderWinsOverTheRecordedOne()
    {
        var info = new SessionInfo { SessionId = Id, Cwd = @"C:\Users\kk\Code\old-place" };
        var live = new LiveSession { SessionId = Id, Cwd = Cwd, Name = "old-place-6c", NameSource = "derived" };

        Assert.Equal(Cwd, SessionNaming.InputsFor(info, live).Cwd);
    }

    /// <summary>
    /// The sweep names sessions it never scanned, so nothing can be claimed about their
    /// content. Saying "empty" would let the policy put a real session on the floor.
    /// </summary>
    [Fact]
    public void AnUnscannedSessionIsNotClaimedToBeEmpty()
    {
        var live = new LiveSession { SessionId = Id, Cwd = Cwd, Name = "vagabond-map-6c", NameSource = "derived" };

        Assert.False(SessionNaming.InputsFor(live).HasContent);
        Assert.False(NamePolicy.WantsOracle(SessionNaming.InputsFor(live), _store));
    }

    [Fact]
    public void TheCollisionSetIsEveryLiveName()
    {
        var live = new Dictionary<string, List<LiveSession>>
        {
            ["a"] = [new LiveSession { SessionId = "a", Name = "vagabond maps" }],
            ["b"] = [new LiveSession { SessionId = "b", Name = "vagabond maps" }],
            ["c"] = [new LiveSession { SessionId = "c", Name = null }],
        };

        Assert.Equal(["vagabond maps", "vagabond maps"], SessionNaming.LiveNamesOf(live));
    }
}
