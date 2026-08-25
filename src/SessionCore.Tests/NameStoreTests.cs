using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The sidecar that tells Sky's names from yours. What matters here is that a record speaks
/// for a name rather than for a session, and that a store it cannot read is never a store it
/// overwrites — forgetting provenance quietly would freeze every name Sky has ever written.
/// </summary>
public class NameStoreTests : IDisposable
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}");

    public NameStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void RoundTripsARecord()
    {
        new NameStore(_dir).Record(Id, "Basemap treatments — vagabond-map", NameOrigin.Title);

        var reloaded = new NameStore(_dir).Get(Id);

        Assert.Equal("Basemap treatments — vagabond-map", reloaded!.Value.Name);
        Assert.Equal(NameOrigin.Title, reloaded.Value.Origin);
    }

    /// <summary>
    /// The whole point of the store: this name is Sky's, and the answer is yes only while
    /// the string on disk is still the string in the registry.
    /// </summary>
    [Fact]
    public void ARecordSpeaksForTheNameItStored()
    {
        var store = new NameStore(_dir);
        store.Record(Id, "Basemap treatments", NameOrigin.Title);

        Assert.Equal(NameOrigin.Title, store.OriginOf(Id, "Basemap treatments"));
        Assert.Null(store.OriginOf(Id, "night shift"));
        Assert.Null(store.OriginOf(Id, null));
        Assert.Null(store.OriginOf("some-other-id", "Basemap treatments"));
    }

    [Fact]
    public void AnUnknownSessionHasNoRecord() =>
        Assert.Null(new NameStore(_dir).Get(Id));

    [Fact]
    public void RecordingAgainReplacesTheRecord()
    {
        var store = new NameStore(_dir);
        store.Record(Id, "Basemap treatments", NameOrigin.Title);
        store.Record(Id, "Rewriting the tile cache", NameOrigin.SelfNamed);

        Assert.Null(store.OriginOf(Id, "Basemap treatments"));
        Assert.Equal(NameOrigin.SelfNamed, new NameStore(_dir).OriginOf(Id, "Rewriting the tile cache"));
    }

    [Fact]
    public void ForgettingLeavesTheNameYours()
    {
        var store = new NameStore(_dir);
        store.Record(Id, "Basemap treatments", NameOrigin.Title);
        store.Forget(Id);

        Assert.Null(new NameStore(_dir).OriginOf(Id, "Basemap treatments"));
    }

    [Fact]
    public void AnEmptyNameIsNotRecorded()
    {
        var store = new NameStore(_dir);
        store.Record(Id, "  ", NameOrigin.Title);

        Assert.Null(store.Get(Id));
    }

    // --- more than one writer -------------------------------------------------

    /// <summary>
    /// A write re-reads the file first, so a record another writer made since startup
    /// survives — the app's background pass must not erase what `SessionCli rename` wrote
    /// while it was holding an older copy in memory.
    /// </summary>
    [Fact]
    public void AWriteDoesNotEraseAnotherWritersRecord()
    {
        var app = new NameStore(_dir);
        var cli = new NameStore(_dir);

        cli.Record("other-session", "Fix the importer", NameOrigin.SelfNamed);
        app.Record(Id, "Basemap treatments", NameOrigin.Title);

        var onDisk = new NameStore(_dir);
        Assert.Equal(NameOrigin.SelfNamed, onDisk.OriginOf("other-session", "Fix the importer"));
        Assert.Equal(NameOrigin.Title, onDisk.OriginOf(Id, "Basemap treatments"));
    }

    /// <summary>One stat per tick is what lets a rename made elsewhere reach the next pass.</summary>
    [Fact]
    public void ReloadPicksUpAnotherWritersRecord()
    {
        var app = new NameStore(_dir);
        Assert.False(app.ReloadIfChanged());

        new NameStore(_dir).Record(Id, "Basemap treatments", NameOrigin.Title);

        Assert.True(app.ReloadIfChanged());
        Assert.Equal(NameOrigin.Title, app.OriginOf(Id, "Basemap treatments"));
    }

    // --- a file that is not what we wrote --------------------------------------

    /// <summary>
    /// Set aside rather than overwritten. Losing provenance is survivable — the shape check
    /// still recognises placeholders — but doing it silently is not.
    /// </summary>
    [Fact]
    public void ACorruptStoreIsMovedAsideAndReported()
    {
        File.WriteAllText(Path_("names.json"), "{ this is not json");

        var store = new NameStore(_dir);

        Assert.Null(store.Get(Id));
        Assert.NotNull(store.LoadWarning);
        Assert.True(File.Exists(Path_("names.json.corrupt")));
    }

    /// <summary>
    /// The file is meant to be readable and correctable by hand, so a hand-written entry has
    /// to load — and an origin from a future Sky reads as the weakest rung rather than
    /// throwing, so this one can still improve on it.
    /// </summary>
    [Fact]
    public void AHandWrittenStoreLoads()
    {
        File.WriteAllText(Path_("names.json"),
            $$"""
            {
              "{{Id}}": { "name": "Basemap treatments", "origin": "title" },
              "other": { "name": "Something else", "origin": "telepathy" }
            }
            """);

        var store = new NameStore(_dir);

        Assert.Equal(NameOrigin.Title, store.OriginOf(Id, "Basemap treatments"));
        Assert.Equal(NameOrigin.Floor, store.OriginOf("other", "Something else"));
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void AnEntryWithNoNameIsSkipped()
    {
        File.WriteAllText(Path_("names.json"), $$"""{ "{{Id}}": { "origin": "title" } }""");

        Assert.Null(new NameStore(_dir).Get(Id));
    }

    [Fact]
    public void EveryOriginSurvivesTheWireFormat()
    {
        foreach (var origin in Enum.GetValues<NameOrigin>())
            Assert.Equal(origin, NameStore.FromWire(NameStore.ToWire(origin)));
    }
}
