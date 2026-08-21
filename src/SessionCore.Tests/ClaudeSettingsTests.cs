using SessionCore;

namespace SessionCore.Tests;

public class ClaudeSettingsTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReturnsBaseId_WhenModelHas1mSuffix()
    {
        var path = WriteTemp("""{"model": "claude-fable-5[1m]"}""");
        try { Assert.Equal("claude-fable-5", ClaudeSettings.ReadLargeModelId(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnsNull_WhenModelHasNoSuffix()
    {
        var path = WriteTemp("""{"model": "claude-fable-5"}""");
        try { Assert.Null(ClaudeSettings.ReadLargeModelId(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnsNull_WhenNoModelConfigured()
    {
        var path = WriteTemp("""{"theme": "dark"}""");
        try { Assert.Null(ClaudeSettings.ReadLargeModelId(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnsNull_WhenFileMissingOrMalformed()
    {
        Assert.Null(ClaudeSettings.ReadLargeModelId(Path.Combine(Path.GetTempPath(), "no-such-settings.json")));

        var path = WriteTemp("not json {");
        try { Assert.Null(ClaudeSettings.ReadLargeModelId(path)); }
        finally { File.Delete(path); }
    }
}
