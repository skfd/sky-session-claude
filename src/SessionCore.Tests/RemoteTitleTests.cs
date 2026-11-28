using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A conversation the phone started under a <c>claude rc</c> host names itself only on
/// claude.ai, through <c>set_session_title</c>. Resuming it after a reboot used to launch it
/// as <c>repo-XX</c>; the transcript's own call is the name to bring back.
/// </summary>
public class RemoteTitleTests
{
    private const string Sid = "b11ff33d-c1b7-5225-a56a-411e64b58d4e";
    private const string Cwd = @"C:\\Users\\kk\\Code\\sc-open-quests";

    private static SessionFileFields Parse(params string[] lines) => SessionFileParser.Parse(lines);

    private static string Prompt(string text) =>
        $$$"""{"type":"user","sessionId":"{{{Sid}}}","cwd":"{{{Cwd}}}","message":{"role":"user","content":"{{{text}}}"}}""";

    private static string GetSelf(string callId) =>
        $$$"""{"type":"assistant","sessionId":"{{{Sid}}}","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"{{{callId}}}","name":"mcp__claude-code-remote__get_session","input":{}}]}}""";

    private static string SelfResult(string callId, string remoteId) =>
        $$$"""{"type":"user","sessionId":"{{{Sid}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{callId}}}","content":[{"type":"text","text":"{\"ccr\":{\"id\":\"{{{remoteId}}}\",\"title\":\"old\"}}"}]}]}}""";

    private static string SetTitle(string remoteId, string title) =>
        $$$"""{"type":"assistant","sessionId":"{{{Sid}}}","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t2","name":"mcp__claude-code-remote__set_session_title","input":{"session_id":"{{{remoteId}}}","title":"{{{title}}}"}}]}}""";

    private static string Custom(string title) =>
        $$$"""{"type":"custom-title","customTitle":"{{{title}}}","sessionId":"{{{Sid}}}"}""";

    private static string Done() =>
        """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}}""";

    [Fact]
    public void OwnSetSessionTitle_IsTheName()
    {
        var f = Parse(Prompt("whats next"), GetSelf("t1"), SelfResult("t1", "session_01ABC"),
            SetTitle("session_01ABC", "What's next — sc-open-quests"), Done());

        Assert.Equal("What's next — sc-open-quests", f.CustomTitle);
        Assert.Equal("What's next — sc-open-quests", f.Name);
    }

    [Fact]
    public void SelfAsTheTarget_IsTheName()
    {
        var f = Parse(Prompt("hi"), SetTitle("self", "Overture switch explained — sc-open-quests"), Done());

        Assert.Equal("Overture switch explained — sc-open-quests", f.CustomTitle);
    }

    [Fact]
    public void BridgeRecord_IdentifiesTheSession()
    {
        var f = Parse(Prompt("hi"),
            """{"type":"bridge-session","bridgeSessionId":"cse_01ABC"}""",
            SetTitle("session_01ABC", "Bridged title"), Done());

        Assert.Equal("Bridged title", f.CustomTitle);
    }

    [Fact]
    public void TitlingAnotherSession_IsIgnored()
    {
        var f = Parse(Prompt("rename that one"), GetSelf("t1"), SelfResult("t1", "session_01ABC"),
            SetTitle("session_01OTHER", "Someone else"), Done());

        Assert.Null(f.CustomTitle);
    }

    [Fact]
    public void WithoutKnowingItsOwnId_NothingIsTaken()
    {
        var f = Parse(Prompt("hi"), SetTitle("session_01ABC", "Maybe mine"), Done());

        Assert.Null(f.CustomTitle);
    }

    [Fact]
    public void ALaterRename_Wins()
    {
        var f = Parse(Prompt("hi"), GetSelf("t1"), SelfResult("t1", "session_01ABC"),
            SetTitle("session_01ABC", "Phone title"), Custom("Renamed later"), Done());

        Assert.Equal("Renamed later", f.CustomTitle);
    }

    [Fact]
    public void ALaterPlaceholder_DoesNotBuryIt()
    {
        // What a resume before this fix wrote: --name sc-open-quests-b1.
        var f = Parse(Prompt("hi"), GetSelf("t1"), SelfResult("t1", "session_01ABC"),
            SetTitle("session_01ABC", "Phone title"), Custom("sc-open-quests-b1"), Done());

        Assert.Equal("Phone title", f.CustomTitle);
    }
}
