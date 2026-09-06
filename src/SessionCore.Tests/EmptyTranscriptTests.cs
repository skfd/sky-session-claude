using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A zero-byte transcript is a session nothing has happened in — no cwd, no title, no turn to
/// date it by. A <c>claude rc</c> host used to pre-create one per project it served, and the
/// file outlives the host, so the list filled up with untitled rows in folders that could not
/// be named. There is nothing in one to show, so it is not a row.
/// </summary>
public class EmptyTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}");
    private readonly SessionScanner _scanner;

    public EmptyTranscriptTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "C--Users-kk-Code-one"));
        _scanner = new SessionScanner(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Add(string id, string content)
    {
        var path = Path.Combine(_root, "C--Users-kk-Code-one", id + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AnEmptyTranscriptIsNotARow()
    {
        Add("aaaa1111-0000-0000-0000-000000000000", "");
        var real = Add("bbbb2222-0000-0000-0000-000000000000", "{}");

        var files = _scanner.SelectFiles(new ScanOptions { All = true, LargeModelId = null });

        Assert.Single(files);
        Assert.Equal(real, files[0].FullName);
    }

    /// <summary>
    /// Nor does it stand in for its project: with the empty file newest, the newest-per-project
    /// view still has to show the session that actually has something in it.
    /// </summary>
    [Fact]
    public void AnEmptyTranscriptDoesNotShadowItsProject()
    {
        var real = Add("bbbb2222-0000-0000-0000-000000000000", "{}");
        File.SetLastWriteTime(real, DateTime.Now.AddHours(-1));
        Add("aaaa1111-0000-0000-0000-000000000000", "");

        var files = _scanner.SelectFiles(new ScanOptions { All = false, LargeModelId = null });

        Assert.Single(files);
        Assert.Equal(real, files[0].FullName);
    }

    /// <summary>
    /// Acting on an id is a different question from listing. A file that is there is found,
    /// so a caller who names one gets told what it is rather than that it does not exist.
    /// </summary>
    [Fact]
    public void AnEmptyTranscriptIsStillFoundByItsId()
    {
        Add("aaaa1111-0000-0000-0000-000000000000", "");

        Assert.Single(_scanner.FindByPrefix("aaaa1111"));
    }
}
