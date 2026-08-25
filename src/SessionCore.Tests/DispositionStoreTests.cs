using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The store has two writers — the app on a keystroke and SessionCli on an agent's behalf —
/// so most of what matters here is what happens when they overlap, and what happens when
/// the file on disk is not what we last wrote.
/// </summary>
public class DispositionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"disp-{Guid.NewGuid():N}");

    public DispositionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void RoundTripsAMark()
    {
        var store = new DispositionStore(_dir);
        store.Set("abc", Disposition.Done);

        Assert.Equal(Disposition.Done, store.Get("abc"));
        Assert.Equal(Disposition.Done, new DispositionStore(_dir).Get("abc"));
    }

    [Fact]
    public void ClearingAMarkRemovesIt()
    {
        var store = new DispositionStore(_dir);
        store.Set("abc", Disposition.Abandoned);
        store.Set("abc", Disposition.None);

        Assert.Equal(Disposition.None, new DispositionStore(_dir).Get("abc"));
    }

    [Fact]
    public void SetManyAppliesToEveryId()
    {
        var store = new DispositionStore(_dir);
        store.SetMany(new[] { "a", "b", "c" }, Disposition.Done);

        var reread = new DispositionStore(_dir);
        Assert.Equal(Disposition.Done, reread.Get("a"));
        Assert.Equal(Disposition.Done, reread.Get("b"));
        Assert.Equal(Disposition.Done, reread.Get("c"));
    }

    // The bug this whole class exists for: two stores are open, and the one that writes
    // second must not flatten the file back to the copy it loaded at startup.
    [Fact]
    public void SecondWriterDoesNotClobberTheFirst()
    {
        var app = new DispositionStore(_dir);
        var cli = new DispositionStore(_dir);   // both loaded an empty store

        app.Set("from-app", Disposition.Done);
        cli.Set("from-cli", Disposition.Abandoned);

        var onDisk = new DispositionStore(_dir);
        Assert.Equal(Disposition.Done, onDisk.Get("from-app"));
        Assert.Equal(Disposition.Abandoned, onDisk.Get("from-cli"));
    }

    // A write reloads first, so the writer also ends up seeing what the other one wrote.
    [Fact]
    public void WritingPicksUpTheOtherWritersMarks()
    {
        var app = new DispositionStore(_dir);
        var cli = new DispositionStore(_dir);

        cli.Set("from-cli", Disposition.Done);
        app.Set("from-app", Disposition.Done);

        Assert.Equal(Disposition.Done, app.Get("from-cli"));
    }

    [Fact]
    public void ReloadIfChangedSeesAnExternalWrite()
    {
        var app = new DispositionStore(_dir);
        Assert.False(app.ReloadIfChanged());        // nothing has happened yet

        new DispositionStore(_dir).Set("elsewhere", Disposition.Done);

        Assert.True(app.ReloadIfChanged());
        Assert.Equal(Disposition.Done, app.Get("elsewhere"));
        Assert.False(app.ReloadIfChanged());        // and it settles
    }

    [Fact]
    public void MigratesTheLegacyAbandonList()
    {
        File.WriteAllText(Path_("abandoned.json"), """["old-1","old-2"]""");

        var store = new DispositionStore(_dir);
        Assert.Equal(Disposition.Abandoned, store.Get("old-1"));
        Assert.Equal(Disposition.Abandoned, store.Get("old-2"));
    }

    [Fact]
    public void LegacyListIsLeftOnDiskForADowngrade()
    {
        File.WriteAllText(Path_("abandoned.json"), """["old-1"]""");

        new DispositionStore(_dir).Set("new-1", Disposition.Done);

        Assert.True(File.Exists(Path_("abandoned.json")));
    }

    // A torn write used to send the loader to the legacy list, which silently reverted
    // every Done mark to the pre-1.9 abandon set. It must not do that any more.
    [Fact]
    public void CorruptStoreIsSetAsideRatherThanFallingBackToLegacy()
    {
        File.WriteAllText(Path_("abandoned.json"), """["old-1"]""");
        File.WriteAllText(Path_("dispositions.json"), """{"abc": "do""");   // truncated

        var store = new DispositionStore(_dir);

        Assert.Equal(Disposition.None, store.Get("old-1"));    // no silent revert
        Assert.NotNull(store.LoadWarning);
        Assert.True(File.Exists(Path_("dispositions.json.corrupt")));
        Assert.False(File.Exists(Path_("dispositions.json")));
    }

    // A store we cannot read is the one case where writing would destroy something: an
    // empty set would replace real marks. The write is refused instead.
    /// <summary>
    /// And the same in memory. A store that cannot be read yields an empty map, and handing
    /// that back to the caller would clear the marks on the cards — the very marks the write
    /// had just refused to clear on disk, so the app would show them gone while the file still
    /// held them.
    /// </summary>
    [Fact]
    public void AnUnreadableStoreDoesNotClearWhatIsAlreadyLoaded()
    {
        new DispositionStore(_dir).Set("keep-me", Disposition.Done);

        var store = new DispositionStore(_dir);
        Assert.Equal(Disposition.Done, store.Get("keep-me"));

        using var _ = new FileStream(Path_("dispositions.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        store.Set("should-not-land", Disposition.Abandoned);

        Assert.Equal(Disposition.Done, store.Get("keep-me"));
        Assert.Equal(Disposition.None, store.Get("should-not-land"));
    }

    [Fact]
    public void AnUnreadableStoreIsNeverOverwritten()
    {
        var store = new DispositionStore(_dir);
        store.Set("keep-me", Disposition.Done);

        using (var _ = new FileStream(Path_("dispositions.json"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = new DispositionStore(_dir);
            blocked.Set("should-not-land", Disposition.Abandoned);
            Assert.NotNull(blocked.LoadWarning);
        }

        var after = new DispositionStore(_dir);
        Assert.Equal(Disposition.Done, after.Get("keep-me"));
        Assert.Equal(Disposition.None, after.Get("should-not-land"));
    }

    [Fact]
    public void AMissingStoreIsNotAWarning()
    {
        Assert.Null(new DispositionStore(_dir).LoadWarning);
    }

    [Fact]
    public void LeavesNoTempFileBehind()
    {
        var store = new DispositionStore(_dir);
        store.Set("abc", Disposition.Done);

        Assert.False(File.Exists(Path_("dispositions.json.tmp")));
    }

    [Fact]
    public void UnknownWireValuesAreIgnored()
    {
        File.WriteAllText(Path_("dispositions.json"),
            """{"a": "done", "b": "banana", "c": "none"}""");

        var store = new DispositionStore(_dir);
        Assert.Equal(Disposition.Done, store.Get("a"));
        Assert.Equal(Disposition.None, store.Get("b"));
        Assert.Equal(Disposition.None, store.Get("c"));
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void AllListsEveryMark()
    {
        var store = new DispositionStore(_dir);
        store.Set("a", Disposition.Done);
        store.Set("b", Disposition.Abandoned);

        Assert.Equal(2, store.All.Count);
        Assert.Equal(Disposition.Abandoned, store.All["b"]);
    }
}
