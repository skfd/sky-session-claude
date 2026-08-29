using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The sidecar agents write their claims into. It shares its machinery with
/// <see cref="DispositionStore"/> — the overlapping-writers cases are pinned next door and
/// are not repeated here — so what this holds is the part only this store has: the file
/// format, and what happens to a word it does not understand.
/// </summary>
public class DeclarationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"decl-{Guid.NewGuid():N}");

    public DeclarationStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static Declaration D(Declared state, string? note = null, string? atTurn = "u1") =>
        new() { State = state, Note = note, AtTurn = atTurn, At = DateTimeOffset.Now };

    [Fact]
    public void RoundTripsAClaim()
    {
        new DeclarationStore(_dir).Set("abc", D(Declared.Exhausted, "pushed; nothing left", "u7"));

        var read = new DeclarationStore(_dir).Get("abc");
        Assert.NotNull(read);
        Assert.Equal(Declared.Exhausted, read!.State);
        Assert.Equal("pushed; nothing left", read.Note);
        Assert.Equal("u7", read.AtTurn);
    }

    [Fact]
    public void ClearingRemovesIt()
    {
        var store = new DeclarationStore(_dir);
        store.Set("abc", D(Declared.Blocked, "the API key"));
        store.Clear("abc");

        Assert.Null(new DeclarationStore(_dir).Get("abc"));
    }

    [Fact]
    public void ASecondClaimReplacesTheFirst()
    {
        var store = new DeclarationStore(_dir);
        store.Set("abc", D(Declared.Blocked, "the API key"));
        store.Set("abc", D(Declared.Exhausted, atTurn: "u2"));

        var read = new DeclarationStore(_dir).Get("abc")!;
        Assert.Equal(Declared.Exhausted, read.State);
        Assert.Null(read.Note);
        Assert.Equal("u2", read.AtTurn);
    }

    // The shape docs/PROJECT-STATE.md specifies, by hand, so a person can still write one.
    [Fact]
    public void ReadsTheDocumentedShape()
    {
        File.WriteAllText(Path_("declarations.json"),
            """
            {
              "abc": { "state": "needs-read", "note": "the audit is in the last message",
                       "atTurn": "u3", "at": "2026-08-29T01:00:00-04:00" }
            }
            """);

        var read = new DeclarationStore(_dir).Get("abc")!;
        Assert.Equal(Declared.NeedsRead, read.State);
        Assert.Equal("the audit is in the last message", read.Note);
        Assert.Equal("u3", read.AtTurn);
    }

    // An entry that decides nothing is worse than no entry: it reads as a session that
    // reported, when nothing was reported. A word from a later build, or a hand-edit that
    // misspelled one, is dropped rather than kept as None.
    [Theory]
    [InlineData("broken")]
    [InlineData("undeclared")]
    [InlineData("exhuasted")]
    [InlineData("")]
    public void AWordWeDoNotUnderstandIsDropped(string word)
    {
        File.WriteAllText(Path_("declarations.json"),
            $$"""{ "abc": { "state": "{{word}}", "atTurn": "u1" } }""");

        Assert.Null(new DeclarationStore(_dir).Get("abc"));
    }

    // Same rule as every sidecar: set the bad file aside and say so, never start fresh in
    // silence and never answer from an empty map as if that were the truth.
    [Fact]
    public void ACorruptStoreIsSetAsideAndReported()
    {
        File.WriteAllText(Path_("declarations.json"), "{ not json");

        var store = new DeclarationStore(_dir);

        Assert.Null(store.Get("abc"));
        Assert.NotNull(store.LoadWarning);
        Assert.True(File.Exists(Path_("declarations.json.corrupt")));
    }

    [Fact]
    public void ReloadsWhenSomeoneElseWrites()
    {
        var mine = new DeclarationStore(_dir);
        Assert.Null(mine.Get("abc"));

        new DeclarationStore(_dir).Set("abc", D(Declared.Runnable, "three files left to sweep"));

        Assert.True(mine.ReloadIfChanged());
        Assert.Equal(Declared.Runnable, mine.Get("abc")!.State);
        Assert.False(mine.ReloadIfChanged());
    }

    [Fact]
    public void LivesBesideTheDispositionsAndNotInThem()
    {
        new DeclarationStore(_dir).Set("abc", D(Declared.Exhausted));
        new DispositionStore(_dir).Set("abc", Disposition.Done);

        Assert.True(File.Exists(Path_("declarations.json")));
        Assert.True(File.Exists(Path_("dispositions.json")));
        Assert.DoesNotContain("exhausted", File.ReadAllText(Path_("dispositions.json")));
    }
}
