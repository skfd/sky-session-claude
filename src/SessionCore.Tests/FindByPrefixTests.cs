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
}
