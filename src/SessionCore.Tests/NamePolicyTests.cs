using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The one decider, and the two properties everything else leans on: it never returns a
/// second answer to a state it produced itself (or the app's background pass is a rename
/// loop), and it never trades a name for a worse one.
/// </summary>
public class NamePolicyTests : IDisposable
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";
    private const string Cwd = @"C:\Users\kk\Code\vagabond-map";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}");
    private readonly NameStore _store;

    public NamePolicyTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new NameStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static LiveSession Live(string? name, string? nameSource = null) => new()
    {
        SessionId = Id,
        Cwd = Cwd,
        Name = name,
        NameSource = nameSource,
    };

    private static NameInputs Inputs(
        LiveSession? live = null,
        string? aiTitle = null,
        string? customTitle = null,
        string? subject = null,
        bool hasContent = true,
        params string[] liveNames) => new()
        {
            SessionId = Id,
            Cwd = Cwd,
            Live = live,
            AiTitle = aiTitle,
            CustomTitle = customTitle,
            Subject = subject,
            HasContent = hasContent,
            LiveNames = liveNames.Length > 0 ? liveNames : live?.Name is { } n ? [n] : [],
        };

    // --- the fixed point ------------------------------------------------------

    /// <summary>
    /// The property the background pass is built on. Renaming appends a custom-title to the
    /// transcript, which wakes the file watcher, which runs the policy again on the state the
    /// rename just produced. A second answer there is not a better name — it is a loop.
    /// </summary>
    [Fact]
    public void ItsOwnRenameIsAFixedPoint()
    {
        var before = Inputs(Live("vagabond-map-69", nameSource: "derived"), aiTitle: "Basemap treatments in Chrome");

        var first = NamePolicy.Decide(before, _store);
        Assert.True(first.HasName);

        // What every Sky name-write does, in the same operation: write the name, record it.
        _store.Record(Id, first.Name!, first.Origin!.Value);

        // And what the watcher then sees: the registry carrying it, and the transcript too.
        var after = Inputs(Live(first.Name), aiTitle: "Basemap treatments in Chrome", customTitle: first.Name);

        var second = NamePolicy.Decide(after, _store);
        Assert.False(second.HasName);
        Assert.Equal("already named that", second.Why);
    }

    /// <summary>
    /// The same, for the floor. A session with nothing to say is renamed once, to a
    /// placeholder that at least stays put, and then left alone forever.
    /// </summary>
    [Fact]
    public void TheFloorSettlesToo()
    {
        var derived = Inputs(Live("vagabond-map-69", nameSource: "derived"), hasContent: false);

        var first = NamePolicy.Decide(derived, _store);
        Assert.Equal("vagabond-map-b9", first.Name);
        _store.Record(Id, first.Name!, first.Origin!.Value);

        Assert.False(NamePolicy.Decide(Inputs(Live(first.Name), customTitle: first.Name), _store).HasName);
    }

    // --- whose name is it -----------------------------------------------------

    [Fact]
    public void ANameYouChoseIsLeftAlone()
    {
        var decision = NamePolicy.Decide(Inputs(Live("night shift"), aiTitle: "Basemap treatments"), _store);

        Assert.False(decision.HasName);
        Assert.Equal("the name is yours", decision.Why);
    }

    /// <summary>
    /// The store speaks for a name, not for a session. Rename it yourself after Sky named it
    /// and the strings diverge, so the record stops applying — no reset gesture, and no way
    /// for last week's record to license overwriting what you just typed.
    /// </summary>
    [Fact]
    public void RenamingItYourselfTakesItBack()
    {
        _store.Record(Id, "Basemap treatments — vagabond-map", NameOrigin.Title);

        var decision = NamePolicy.Decide(
            Inputs(Live("night shift"), aiTitle: "Basemap treatments"), _store);

        Assert.False(decision.HasName);
        Assert.Equal("the name is yours", decision.Why);
    }

    /// <summary>
    /// A name recorded as yours survives the shape check. "vagabond-map-99" is exactly what a
    /// placeholder looks like, and IsFloor cannot tell the difference — the record can.
    /// </summary>
    [Fact]
    public void ANameYouChoseThatLooksLikeAPlaceholderIsStillYours()
    {
        _store.Record(Id, "vagabond-map-99", NameOrigin.Chosen);

        Assert.False(NamePolicy.Decide(
            Inputs(Live("vagabond-map-99"), aiTitle: "Basemap treatments"), _store).HasName);
    }

    /// <summary>
    /// The registry records nameSource exactly when the CLI invented the name, so this needs
    /// no guessing at all — and it is how most sessions in the wild present.
    /// </summary>
    [Theory]
    [InlineData("derived")]
    [InlineData("collision")]
    public void AnInventedNameIsReplacedByTheTitle(string source)
    {
        var decision = NamePolicy.Decide(
            Inputs(Live("vagabond-map-69", source), aiTitle: "Basemap treatments in Chrome"), _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", decision.Name);
        Assert.Equal(NameOrigin.Title, decision.Origin);
    }

    /// <summary>
    /// History written before the store existed: Sky's own slug reached the transcript as a
    /// custom-title, so it reads back with nameSource absent — indistinguishable from a name
    /// you typed except by its shape.
    /// </summary>
    [Fact]
    public void APlaceholderFromBeforeTheStoreIsStillRecognised()
    {
        var decision = NamePolicy.Decide(
            Inputs(Live("vagabond-map-b9"), aiTitle: "Basemap treatments in Chrome",
                   customTitle: "vagabond-map-b9"),
            _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", decision.Name);
    }

    // --- the ladder -----------------------------------------------------------

    /// <summary>
    /// A session that named itself knows what it is doing now; its aiTitle was written once,
    /// early, and never revisited. Demoting one to the other would undo the best source there
    /// is on the next background pass.
    /// </summary>
    [Fact]
    public void ASelfNamedSessionIsNotDemotedToItsTitle()
    {
        _store.Record(Id, "Rewriting the tile cache — vagabond-map", NameOrigin.SelfNamed);

        var decision = NamePolicy.Decide(
            Inputs(Live("Rewriting the tile cache — vagabond-map"), aiTitle: "Start Chrome"), _store);

        Assert.False(decision.HasName);
    }

    [Fact]
    public void ATitledSessionIsNotDemotedToTheFloor()
    {
        _store.Record(Id, "Basemap treatments — vagabond-map", NameOrigin.Title);

        var decision = NamePolicy.Decide(Inputs(Live("Basemap treatments — vagabond-map")), _store);

        Assert.False(decision.HasName);
    }

    /// <summary>A subject read out of the conversation just now beats a title written once, early.</summary>
    [Fact]
    public void AnOracleSubjectOutranksTheTitle()
    {
        _store.Record(Id, "Start Chrome — vagabond-map", NameOrigin.Title);

        var decision = NamePolicy.Decide(
            Inputs(Live("Start Chrome — vagabond-map"), aiTitle: "Start Chrome",
                   subject: "Basemap treatments in Chrome"),
            _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", decision.Name);
        Assert.Equal(NameOrigin.Oracle, decision.Origin);
    }

    /// <summary>
    /// The CLI redraws its suffix on every launch, so the one part of the name carrying no
    /// meaning is the part that keeps changing. Ours is the id prefix, which does not.
    /// </summary>
    [Fact]
    public void APlaceholderIsStillSwappedForAStableOne()
    {
        var decision = NamePolicy.Decide(Inputs(Live("vagabond-map-69", "derived"), hasContent: false), _store);

        Assert.Equal("vagabond-map-b9", decision.Name);
        Assert.Equal(NameOrigin.Floor, decision.Origin);
    }

    // --- collisions -----------------------------------------------------------

    /// <summary>
    /// The one place Sky overwrites something you typed. Three live sessions all reading
    /// "vagabond maps" identify none of them.
    /// </summary>
    [Fact]
    public void ACollidingChosenNameLosesToTheSubject()
    {
        var decision = NamePolicy.Decide(
            Inputs(Live("vagabond maps"), aiTitle: "Basemap treatments in Chrome",
                   liveNames: ["vagabond maps", "vagabond maps", "vagabond maps"]),
            _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", decision.Name);
        Assert.Contains("shares", decision.Why);
    }

    /// <summary>
    /// And the limit on it: the override fires only when there is a subject to write.
    /// Replacing "vagabond maps" with "vagabond-map-b9" trades a collision for a name that
    /// says less, which is not a repair.
    /// </summary>
    [Fact]
    public void ACollidingChosenNameWithNothingToSayIsKept()
    {
        var decision = NamePolicy.Decide(
            Inputs(Live("vagabond maps"), liveNames: ["vagabond maps", "vagabond maps"]),
            _store);

        Assert.False(decision.HasName);
        Assert.Contains("nothing better", decision.Why);
    }

    /// <summary>One session holding a name is not a collision, however many rows are listed.</summary>
    [Fact]
    public void AUniqueChosenNameIsNotACollision()
    {
        var decision = NamePolicy.Decide(
            Inputs(Live("vagabond maps"), aiTitle: "Basemap treatments",
                   liveNames: ["vagabond maps", "night shift", "code-20"]),
            _store);

        Assert.False(decision.HasName);
    }

    /// <summary>A session that is not running is in no registry, so it collides with nothing.</summary>
    [Fact]
    public void AClosedSessionDoesNotCollide()
    {
        var decision = NamePolicy.Decide(
            Inputs(customTitle: "vagabond maps", aiTitle: "Basemap treatments",
                   liveNames: ["vagabond maps", "vagabond maps"]),
            _store);

        Assert.False(decision.HasName);
    }

    // --- sessions with no name at all -----------------------------------------

    /// <summary>
    /// A closed session that was never named takes whatever is going — this is the path a
    /// resume goes through, where the name is supplied on the command line.
    /// </summary>
    [Fact]
    public void AnUnnamedSessionTakesItsTitle()
    {
        var decision = NamePolicy.Decide(Inputs(aiTitle: "Basemap treatments in Chrome"), _store);

        Assert.Equal("Basemap treatments in Chrome — vagabond-map", decision.Name);
    }

    [Fact]
    public void AnUnnamedSessionWithNothingToSayTakesTheFloor()
    {
        var decision = NamePolicy.Decide(Inputs(hasContent: false), _store);

        Assert.Equal("vagabond-map-b9", decision.Name);
        Assert.Equal(NameOrigin.Floor, decision.Origin);
    }

    /// <summary>
    /// A slug Sky wrote into a transcript must not come back as a subject: composed into
    /// "Vagabond-map-b9 — vagabond-map" it would become a title, and a title is never
    /// replaced by the floor again. That is the self-reinforcing loop, one step later.
    /// </summary>
    [Fact]
    public void APlaceholderIsNeverComposedIntoATitle()
    {
        var decision = NamePolicy.Decide(
            Inputs(aiTitle: "vagabond-map-b9", hasContent: false), _store);

        Assert.Equal("vagabond-map-b9", decision.Name);
        Assert.Equal(NameOrigin.Floor, decision.Origin);
    }

    // --- when an oracle earns its money ---------------------------------------

    [Fact]
    public void AnOracleIsWantedForAPlaceholderOnASessionThatDidSomething() =>
        Assert.True(NamePolicy.WantsOracle(Inputs(Live("vagabond-map-69", "derived")), _store));

    /// <summary>A title is free and already in the file; paying to read the same thing is waste.</summary>
    [Fact]
    public void AnOracleIsNotWantedWhenThereIsATitle() =>
        Assert.False(NamePolicy.WantsOracle(
            Inputs(Live("vagabond-map-69", "derived"), aiTitle: "Basemap treatments"), _store));

    /// <summary>Nothing happened in it, so there is nothing in there to read.</summary>
    [Fact]
    public void AnOracleIsNotWantedForAnEmptySession() =>
        Assert.False(NamePolicy.WantsOracle(
            Inputs(Live("vagabond-map-69", "derived"), hasContent: false), _store));

    /// <summary>Step 7 of the plan in one line: only when self-naming did not happen.</summary>
    [Fact]
    public void AnOracleIsNotWantedForASessionThatNamedItself()
    {
        _store.Record(Id, "Rewriting the tile cache — vagabond-map", NameOrigin.SelfNamed);

        Assert.False(NamePolicy.WantsOracle(
            Inputs(Live("Rewriting the tile cache — vagabond-map")), _store));
    }

    /// <summary>Asking twice would flap between two readings of the same conversation.</summary>
    [Fact]
    public void AnOracleIsNotWantedTwice()
    {
        _store.Record(Id, "Basemap treatments — vagabond-map", NameOrigin.Oracle);

        Assert.False(NamePolicy.WantsOracle(
            Inputs(Live("Basemap treatments — vagabond-map")), _store));
    }

    [Fact]
    public void AnOracleIsNotWantedForANameYouChose() =>
        Assert.False(NamePolicy.WantsOracle(Inputs(Live("night shift")), _store));

    /// <summary>
    /// Except when it collides: the override needs a subject, and this is where paying for
    /// one is the difference between repairing the collision and living with it.
    /// </summary>
    [Fact]
    public void AnOracleIsWantedForAChosenNameThatCollides() =>
        Assert.True(NamePolicy.WantsOracle(
            Inputs(Live("vagabond maps"), liveNames: ["vagabond maps", "vagabond maps"]), _store));
}
