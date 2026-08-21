using System.Text.Json;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the fork-from-point mechanics: which prompts count as fork points, and that
/// a fork keeps exactly the chosen ancestry under a fresh session id while leaving
/// the original file untouched.
/// </summary>
public class SessionForkerTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";

    private static string J(string s) => JsonSerializer.Serialize(s);

    private static string User(string uuid, string? parent, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"parentUuid\":{(parent is null ? "null" : J(parent))}," +
        $"\"sessionId\":\"{Sid}\",\"timestamp\":\"2026-08-21T10:00:00.000Z\"," +
        $"\"message\":{{\"role\":\"user\",\"content\":{J(text)}}}}}";

    private static string Asst(string uuid, string parent, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"parentUuid\":{J(parent)}," +
        $"\"sessionId\":\"{Sid}\"," +
        $"\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{J(text)}}}]}}}}";

    private static string ToolResult(string uuid, string parent) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"parentUuid\":{J(parent)},\"sessionId\":\"{Sid}\"," +
        "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}";

    private static string WriteSession(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fork-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>u1 → a1 → u2 → a2, plus mode/snapshot noise.</summary>
    private static string[] TwoPromptSession() =>
    [
        $"{{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"{Sid}\"}}",
        User("u1", null, "first ask"),
        Asst("a1", "u1", "First answer."),
        $"{{\"type\":\"file-history-snapshot\",\"messageId\":\"m1\",\"snapshot\":{{}}}}",
        User("u2", "a1", "second ask"),
        Asst("a2", "u2", "Second answer."),
        $"{{\"type\":\"last-prompt\",\"lastPrompt\":\"second ask\",\"leafUuid\":\"a2\",\"sessionId\":\"{Sid}\"}}",
    ];

    [Fact]
    public void ListForkPoints_FindsPromptsWithAliveParents()
    {
        var path = WriteSession(TwoPromptSession());
        var points = SessionForker.ListForkPoints(path);

        // Prompt #1 has no parent record — nothing before it to fork from.
        var p = Assert.Single(points);
        Assert.Equal("a1", p.LeafUuid);
        Assert.Equal("second ask", p.Prompt);
        Assert.Equal(2, p.Ordinal);
        Assert.NotNull(p.Timestamp);
    }

    [Fact]
    public void ListForkPoints_SkipsHarnessToolResultAndInterruptRecords()
    {
        var path = WriteSession(
            User("u1", null, "real ask"),
            Asst("a1", "u1", "Working on it"),
            ToolResult("t1", "a1"),
            Asst("a2", "t1", "Done."),
            User("u2", "a2", "<system-reminder>injected</system-reminder>"),
            User("u3", "a2", "[Request interrupted by user]"),
            User("u4", "a2", "follow-up ask"));

        var points = SessionForker.ListForkPoints(path);
        var p = Assert.Single(points);
        Assert.Equal("follow-up ask", p.Prompt);
        Assert.Equal("a2", p.LeafUuid);
    }

    [Fact]
    public void ForkFrom_KeepsAncestryOnly_UnderFreshId()
    {
        var path = WriteSession(TwoPromptSession());
        var newId = SessionForker.ForkFrom(path, "a1");

        Assert.True(Guid.TryParse(newId, out _));
        var forkPath = Path.Combine(Path.GetDirectoryName(path)!, newId + ".jsonl");
        Assert.True(File.Exists(forkPath));

        var text = File.ReadAllText(forkPath);
        Assert.Contains("first ask", text);
        Assert.Contains("First answer.", text);
        Assert.DoesNotContain("second ask", text);     // beyond the cut
        Assert.DoesNotContain("Second answer.", text);
        Assert.DoesNotContain("file-history-snapshot", text);
        Assert.DoesNotContain(Sid, text);              // every sessionId rewritten

        // The appended leaf pointer names the fork point and the new session.
        var last = File.ReadLines(forkPath).Last();
        using var doc = JsonDocument.Parse(last);
        Assert.Equal("last-prompt", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("a1", doc.RootElement.GetProperty("leafUuid").GetString());
        Assert.Equal(newId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("first ask", doc.RootElement.GetProperty("lastPrompt").GetString());
    }

    [Fact]
    public void ForkFrom_NeverTouchesTheOriginal()
    {
        var path = WriteSession(TwoPromptSession());
        var before = File.ReadAllText(path);
        SessionForker.ForkFrom(path, "a1");
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void ForkFrom_UnknownLeaf_Throws()
    {
        var path = WriteSession(TwoPromptSession());
        Assert.Throws<InvalidOperationException>(() => SessionForker.ForkFrom(path, "nope"));
    }
}
