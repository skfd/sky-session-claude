using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locating a session by id is a directory walk, not a scan: no session file is opened, so
/// acting on one named session costs milliseconds however many are on disk. A prefix works
/// like a short commit sha, and the caller decides what an ambiguous one means.
/// </summary>
public class FindByPrefixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
    private readonly SessionScanner _scanner;

    public FindByPrefixTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "C--Users-kk-Code-one"));
        Directory.CreateDirectory(Path.Combine(_root, "C--Users-kk-Code-two"));
        _scanner = new SessionScanner(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Add(string project, string id) =>
        File.WriteAllText(Path.Combine(_root, project, id + ".jsonl"), "{}");

    [Fact]
    public void FindsAnExactId()
    {
        Add("C--Users-kk-Code-one", "aaaa1111-0000-0000-0000-000000000000");

        var found = _scanner.FindByPrefix("aaaa1111-0000-0000-0000-000000000000");
        Assert.Single(found);
    }

    [Fact]
    public void FindsByPrefixAcrossProjects()
    {
        Add("C--Users-kk-Code-one", "aaaa1111-0000-0000-0000-000000000000");
        Add("C--Users-kk-Code-two", "aaaa2222-0000-0000-0000-000000000000");

        Assert.Equal(2, _scanner.FindByPrefix("aaaa").Count);
        Assert.Single(_scanner.FindByPrefix("aaaa1"));
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        Add("C--Users-kk-Code-one", "abcd1111-0000-0000-0000-000000000000");
        Assert.Single(_scanner.FindByPrefix("ABCD"));
    }

    [Fact]
    public void ReturnsNothingForAnUnknownPrefix()
    {
        Add("C--Users-kk-Code-one", "aaaa1111-0000-0000-0000-000000000000");
        Assert.Empty(_scanner.FindByPrefix("zzzz"));
    }

    [Fact]
    public void IgnoresSubagentTranscriptsNestedDeeper()
    {
        var nested = Path.Combine(_root, "C--Users-kk-Code-one", "aaaa1111", "subagents");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "aaaa1111-agent-1.jsonl"), "{}");

        Assert.Empty(_scanner.FindByPrefix("aaaa1111"));
    }

    // A prefix is a uuid fragment, never a pattern of its own — otherwise `done "*"` would
    // mark every session on the machine.
    [Theory]
    [InlineData("*")]
    [InlineData("aa*")]
    [InlineData("?")]
    [InlineData("../../etc")]
    public void RefusesGlobsAndPaths(string prefix)
    {
        Add("C--Users-kk-Code-one", "aaaa1111-0000-0000-0000-000000000000");
        Assert.Empty(_scanner.FindByPrefix(prefix));
    }

    [Fact]
    public void RefusesAnEmptyPrefix()
    {
        Add("C--Users-kk-Code-one", "aaaa1111-0000-0000-0000-000000000000");
        Assert.Empty(_scanner.FindByPrefix("  "));
    }

    // --- one id, one file ----------------------------------------------------
    //
    // A conversation resumed from another folder, or one whose cwd changes part way
    // through, is written to a second project folder under the same uuid. Everything
    // downstream keys on the id, so two files for one session is a duplicate key in a
    // dictionary of rows and an ambiguity no longer prefix could ever resolve.

    private const string Duplicated = "aaaa1111-0000-0000-0000-000000000000";

    private void AddBothProjects()
    {
        Add("C--Users-kk-Code-one", Duplicated);
        Add("C--Users-kk-Code-two", Duplicated);
        // The copy the session is still appending to is the newer one.
        File.SetLastWriteTime(
            Path.Combine(_root, "C--Users-kk-Code-two", Duplicated + ".jsonl"),
            DateTime.Now.AddHours(-3));
    }

    [Fact]
    public void CollapsesOneSessionWrittenUnderTwoProjects()
    {
        AddBothProjects();

        var found = Assert.Single(_scanner.FindByPrefix(Duplicated));
        Assert.Equal("C--Users-kk-Code-one", found.Directory!.Name);
    }

    [Fact]
    public void ScanReturnsOneRowPerSessionId()
    {
        AddBothProjects();

        var rows = _scanner.Scan(new ScanOptions());
        Assert.Single(rows);
        Assert.Equal(Duplicated, rows[0].SessionId);
    }
}
