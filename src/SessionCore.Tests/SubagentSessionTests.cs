using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// End-to-end scans over a synthetic ~/.claude/projects tree that mirrors the real
/// on-disk layout, including subagent transcripts. Since ~v2.x Claude Code writes
/// each Agent-tool subagent's transcript to
/// <c>&lt;projects&gt;/&lt;flat-project&gt;/&lt;session-id&gt;/subagents/agent-&lt;id&gt;.jsonl</c>.
/// Those files are not resumable (`claude --resume agent-...` is rejected: not a
/// UUID) and never carry ai-title/custom-title records, so listing them produces
/// "(untitled)" cards with dead resume commands. The scanner must treat them as
/// part of their parent session, not as sessions of their own.
/// </summary>
public class SubagentSessionTests : IDisposable
{
    private readonly string _root;

    public SubagentSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sky-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // --- synthetic file builders, shaped like real records on disk -----------

    private string ProjectDir(string flatName)
    {
        var dir = Path.Combine(_root, flatName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A minimal but realistic main session: title, prompt, agent reply.</summary>
    private static string WriteMainSession(string projectDir, string sessionId, string title,
        string cwd, DateTime lastWrite)
    {
        var path = Path.Combine(projectDir, sessionId + ".jsonl");
        var cwdJson = cwd.Replace("\\", "\\\\");
        File.WriteAllLines(path, new[]
        {
            $"{{\"type\":\"ai-title\",\"aiTitle\":\"{title}\"}}",
            $"{{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"user\",\"cwd\":\"{cwdJson}\",\"sessionId\":\"{sessionId}\",\"message\":{{\"role\":\"user\",\"content\":\"fix the tests\"}}}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-x\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done, all tests pass.\"}],\"usage\":{\"input_tokens\":1000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}",
        });
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    /// <summary>
    /// A subagent transcript under &lt;session-id&gt;/subagents/, with the record shape
    /// observed in real files: isSidechain:true, agentId, sessionId of the parent,
    /// a long injected task prompt, and no title records.
    /// </summary>
    private static string WriteSubagentTranscript(string projectDir, string parentSessionId,
        string agentId, string cwd, DateTime lastWrite)
    {
        var dir = Path.Combine(projectDir, parentSessionId, "subagents");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"agent-{agentId}.jsonl");
        var cwdJson = cwd.Replace("\\", "\\\\");
        File.WriteAllLines(path, new[]
        {
            $"{{\"parentUuid\":null,\"isSidechain\":true,\"agentId\":\"{agentId}\",\"type\":\"user\",\"cwd\":\"{cwdJson}\",\"sessionId\":\"{parentSessionId}\",\"message\":{{\"role\":\"user\",\"content\":\"You are doing a scenario-coverage review. Read the briefing file and report back only a short summary.\"}}}}",
            $"{{\"parentUuid\":\"x\",\"isSidechain\":true,\"agentId\":\"{agentId}\",\"type\":\"assistant\",\"message\":{{\"role\":\"assistant\",\"model\":\"claude-x\",\"stop_reason\":\"end_turn\",\"content\":[{{\"type\":\"text\",\"text\":\"Review complete, 2 findings filed.\"}}],\"usage\":{{\"input_tokens\":50000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}}}}",
        });
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    // --- the tests -----------------------------------------------------------

    [Fact]
    public void Scan_ListsOnlyRealSessions_NotSubagentTranscripts()
    {
        var proj = ProjectDir("C--Users-kk-Code-myrepo");
        var cwd = @"C:\Users\kk\Code\myrepo";
        var sid = "11111111-1111-1111-1111-111111111111";
        WriteMainSession(proj, sid, "Fix the tests", cwd, DateTime.Now.AddMinutes(-10));
        WriteSubagentTranscript(proj, sid, "aaaa111122223333f", cwd, DateTime.Now.AddMinutes(-5));
        WriteSubagentTranscript(proj, sid, "bbbb111122223333f", cwd, DateTime.Now.AddMinutes(-4));

        var rows = new SessionScanner(_root).Scan(new ScanOptions());

        Assert.Single(rows);                      // 1 session, not 3
        Assert.Equal(sid, rows[0].SessionId);
        Assert.Equal("Fix the tests", rows[0].Name);
    }

    [Fact]
    public void Scan_TopCap_IsNotConsumedBySubagentTranscripts()
    {
        // Subagent files are typically the newest files on disk while a fleet is
        // running; with a Top cap they must not crowd real sessions off the list.
        var proj = ProjectDir("C--Users-kk-Code-myrepo");
        var cwd = @"C:\Users\kk\Code\myrepo";
        var sid = "22222222-2222-2222-2222-222222222222";
        WriteMainSession(proj, sid, "Main work", cwd, DateTime.Now.AddHours(-1));
        for (int i = 0; i < 3; i++)
            WriteSubagentTranscript(proj, sid, $"cccc11112222333{i:x}f"[..17], cwd, DateTime.Now.AddMinutes(-i));

        var rows = new SessionScanner(_root).Scan(new ScanOptions { Top = 2 });

        Assert.Contains(rows, r => r.SessionId == sid);
    }

    [Fact]
    public void NewestPerProject_PicksNewestRealSession_NotANewerSubagentFile()
    {
        var proj = ProjectDir("C--Users-kk-Code-myrepo");
        var cwd = @"C:\Users\kk\Code\myrepo";
        var older = "33333333-3333-3333-3333-333333333333";
        var newer = "44444444-4444-4444-4444-444444444444";
        WriteMainSession(proj, older, "Older session", cwd, DateTime.Now.AddHours(-3));
        WriteMainSession(proj, newer, "Newer session", cwd, DateTime.Now.AddHours(-2));
        // Subagent of the *older* session written most recently of all.
        WriteSubagentTranscript(proj, older, "dddd111122223333f", cwd, DateTime.Now.AddMinutes(-1));

        var rows = new SessionScanner(_root).Scan(new ScanOptions { All = false });

        Assert.Single(rows);
        Assert.Equal(newer, rows[0].SessionId);
    }

    [Fact]
    public void Scan_NeverEmitsAResumeCommandThatTheCliRejects()
    {
        // `claude --resume agent-<id>` fails: "not a UUID and does not match any
        // session title". No row may carry such a command.
        var proj = ProjectDir("C--Users-kk-Code-myrepo");
        var cwd = @"C:\Users\kk\Code\myrepo";
        var sid = "55555555-5555-5555-5555-555555555555";
        WriteMainSession(proj, sid, "Main work", cwd, DateTime.Now.AddMinutes(-10));
        WriteSubagentTranscript(proj, sid, "eeee111122223333f", cwd, DateTime.Now.AddMinutes(-5));

        var rows = new SessionScanner(_root).Scan(new ScanOptions());

        Assert.DoesNotContain(rows, r => r.Command.Contains("--resume agent-"));
    }
}
