using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the 7-state classifier and field extraction against crafted transcripts.
/// Each fixture is the minimal set of JSONL records that drives one branch.
/// </summary>
public class ClassifierTests
{
    private static TranscriptFields Parse(params string[] lines) =>
        TranscriptParser.Parse(lines);

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string Asst(string stopReason, string? text = null, bool toolUse = false,
        string model = "claude-sonnet-4-6", bool synthetic = false, bool apiError = false,
        int input = 0, int cacheCreate = 0, int cacheRead = 0)
    {
        var content = new List<string>();
        if (text is not null) content.Add("{\"type\":\"text\",\"text\":" + J(text) + "}");
        if (toolUse) content.Add("{\"type\":\"tool_use\",\"name\":\"Bash\"}");
        var usage = "{\"input_tokens\":" + input
            + ",\"cache_creation_input_tokens\":" + cacheCreate
            + ",\"cache_read_input_tokens\":" + cacheRead + "}";
        var m = synthetic ? "<synthetic>" : model;
        var apiErrField = apiError ? ",\"isApiErrorMessage\":true" : "";
        return "{\"type\":\"assistant\"" + apiErrField + ",\"message\":{\"model\":" + J(m)
            + ",\"stop_reason\":" + J(stopReason)
            + ",\"content\":[" + string.Join(",", content) + "],\"usage\":" + usage + "}}";
    }

    private static string User(string text, bool toolResult = false)
    {
        if (toolResult)
            return "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}";
        return "{\"type\":\"user\",\"message\":{\"content\":" + J(text) + "}}";
    }

    [Fact]
    public void Complete_WhenAssistantEndsWithStatement()
    {
        var f = Parse(User("do it"), Asst("end_turn", "Done, all tests pass."));
        Assert.Equal(SessionStatus.Complete, f.Status);
    }

    [Fact]
    public void WaitingYou_WhenAssistantEndsWithQuestion()
    {
        var f = Parse(User("do it"), Asst("end_turn", "Which option do you prefer?"));
        Assert.Equal(SessionStatus.WaitingYou, f.Status);
    }

    [Fact]
    public void CutOff_WhenAssistantStopsOnToolUse()
    {
        var f = Parse(User("do it"), Asst("tool_use", "Running the build", toolUse: true));
        Assert.Equal(SessionStatus.CutOff, f.Status);
    }

    [Fact]
    public void CutOff_WhenAssistantHitsMaxTokens()
    {
        var f = Parse(User("do it"), Asst("max_tokens", "A very long answer that got cut"));
        Assert.Equal(SessionStatus.CutOff, f.Status);
    }

    [Fact]
    public void WaitingAgent_WhenUserSpokeLast()
    {
        var f = Parse(Asst("end_turn", "Done."), User("now do the next thing"));
        Assert.Equal(SessionStatus.WaitingAgent, f.Status);
    }

    [Fact]
    public void CutOff_WhenLastRecordIsToolResult()
    {
        var f = Parse(Asst("tool_use", "Running", toolUse: true), User("", toolResult: true));
        Assert.Equal(SessionStatus.CutOff, f.Status);
    }

    [Fact]
    public void Interrupted_WhenUserInterrupted()
    {
        var f = Parse(Asst("end_turn", "Working"), User("[Request interrupted by user for tool use]"));
        Assert.Equal(SessionStatus.Interrupted, f.Status);
    }

    [Fact]
    public void Limit_WhenSyntheticErrorMentionsUsageLimit()
    {
        var f = Parse(User("go"), Asst("end_turn", "You have hit your weekly usage limit.", synthetic: true));
        Assert.Equal(SessionStatus.Limit, f.Status);
    }

    [Fact]
    public void Error_WhenSyntheticErrorIsGeneric()
    {
        var f = Parse(User("go"), Asst("end_turn", "API error: overloaded.", apiError: true));
        Assert.Equal(SessionStatus.Error, f.Status);
    }

    [Fact]
    public void ContextPct_ComputedFromLastRealTurnUsage()
    {
        // 20000 + 30000 + 50000 = 100000 tokens / 200000 window = 50%
        var f = Parse(Asst("end_turn", "done", input: 20000, cacheCreate: 30000, cacheRead: 50000));
        Assert.Equal(100000, f.ContextTokens);
        Assert.Equal(50, f.ContextPct);
    }

    [Fact]
    public void ContextPct_NullWhenNoUsage()
    {
        var f = Parse(User("hi"));
        Assert.Null(f.ContextPct);
    }

    [Fact]
    public void CustomTitle_WinsOverAiTitle()
    {
        var f = Parse(
            """{"type":"ai-title","aiTitle":"AI guess"}""",
            """{"type":"custom-title","customTitle":"My title"}""",
            Asst("end_turn", "done"));
        Assert.Equal("My title", f.Name);
    }

    [Fact]
    public void Recap_PrefersAwaySummaryOverLastText()
    {
        var f = Parse(
            Asst("end_turn", "last assistant reply"),
            """{"type":"system","subtype":"away_summary","content":"the recap summary"}""");
        Assert.Equal("the recap summary", f.Recap);
    }

    [Fact]
    public void LastPrompt_FallsBackToUserTextWhenNoLastPromptRecord()
    {
        var f = Parse(User("my only prompt"), Asst("end_turn", "ok"));
        Assert.Equal("my only prompt", f.LastPrompt);
    }

    [Fact]
    public void LastPromptRecord_WinsOverUserText()
    {
        var f = Parse(
            User("older inline user text"),
            """{"type":"last-prompt","lastPrompt":"the real last prompt"}""",
            Asst("end_turn", "ok"));
        Assert.Equal("the real last prompt", f.LastPrompt);
    }

    [Fact]
    public void Cwd_ExtractedFromAnyRecord()
    {
        var f = Parse(
            """{"type":"system","subtype":"local_command","content":"x","cwd":"C:\\Users\\kk\\Code\\proj"}""",
            Asst("end_turn", "done"));
        Assert.Equal("C:\\Users\\kk\\Code\\proj", f.Cwd);
    }
}
