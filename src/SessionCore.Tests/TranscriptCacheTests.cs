using SessionCore;

namespace SessionCore.Tests;

public class TranscriptCacheTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sess-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReturnsCachedInstance_WhenFileUnchanged()
    {
        var path = WriteTemp("{\"type\":\"ai-title\",\"aiTitle\":\"First\"}");
        try
        {
            var cache = new TranscriptCache();
            var a = cache.GetOrParse(new FileInfo(path), 200_000);
            var b = cache.GetOrParse(new FileInfo(path), 200_000);
            Assert.Same(a, b);           // record instance reused, not re-parsed
            Assert.Equal(1, cache.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReparsesWhenFileChanges()
    {
        var path = WriteTemp("{\"type\":\"ai-title\",\"aiTitle\":\"First\"}");
        try
        {
            var cache = new TranscriptCache();
            var a = cache.GetOrParse(new FileInfo(path), 200_000);
            Assert.Equal("First", a.Name);

            // Rewrite with a different title and a bumped write time.
            File.WriteAllText(path, "{\"type\":\"ai-title\",\"aiTitle\":\"Second\"}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

            var b = cache.GetOrParse(new FileInfo(path), 200_000);
            Assert.Equal("Second", b.Name);
            Assert.NotSame(a, b);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReparsesWhenContextWindowChanges()
    {
        // Same file, different window -> different Ctx%, so must not reuse the cache.
        var path = WriteTemp(
            "{\"type\":\"assistant\",\"message\":{\"model\":\"m\",\"stop_reason\":\"end_turn\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"done\"}]," +
            "\"usage\":{\"input_tokens\":100000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}");
        try
        {
            var cache = new TranscriptCache();
            var a = cache.GetOrParse(new FileInfo(path), 200_000);
            var b = cache.GetOrParse(new FileInfo(path), 1_000_000);
            Assert.Equal(50, a.ContextPct);
            Assert.Equal(10, b.ContextPct);
        }
        finally { File.Delete(path); }
    }
}
