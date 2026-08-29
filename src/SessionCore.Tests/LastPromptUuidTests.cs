using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the anchor a declaration expires against.
///
/// The whole point of this field is that it does <i>not</i> move when the agent goes on
/// working. An agent declares its state mid-turn — the tool call, its result and the closing
/// message are all written afterwards — so a claim anchored to the last turn would be stale
/// before the session ended, and every declaration the convention produces would be worthless.
/// What these tests pin is which records count as the operator speaking and which do not.
/// </summary>
public class LastPromptUuidTests
{
    private static SessionFileFields Parse(params string[] lines) =>
        SessionFileParser.Parse(lines);

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string User(string text, string? uuid = null) =>
        "{\"type\":\"user\"" + Uuid(uuid) + ",\"message\":{\"content\":" + J(text) + "}}";

    private static string ToolResult(string? uuid = null) =>
        "{\"type\":\"user\"" + Uuid(uuid)
        + ",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}";

    private static string Asst(string text, string? uuid = null, bool toolUse = false) =>
        "{\"type\":\"assistant\"" + Uuid(uuid) + ",\"message\":{\"model\":\"claude-opus-5\","
        + "\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":" + J(text) + "}"
        + (toolUse ? ",{\"type\":\"tool_use\",\"name\":\"Bash\"}" : "") + "]}}";

    private static string Uuid(string? uuid) =>
        uuid is null ? "" : ",\"uuid\":" + J(uuid);

    [Fact]
    public void IsTheLastOperatorPrompt()
    {
        var f = Parse(
            User("first", "u1"),
            Asst("sure", "a1"),
            User("second", "u2"),
            Asst("done", "a2"));

        Assert.Equal("u2", f.LastPromptUuid);
    }

    // The reason the field exists: everything an agent writes after being asked leaves the
    // anchor where it was, so a declaration made mid-turn survives its own closing message.
    [Fact]
    public void AgentTurnsAfterThePromptDoNotMoveIt()
    {
        var f = Parse(
            User("go", "u1"),
            Asst("running it", "a1", toolUse: true),
            ToolResult("t1"),
            Asst("all done.", "a2"));

        Assert.Equal("u1", f.LastPromptUuid);
    }

    [Fact]
    public void ToolResultsAreNotTheOperator()
    {
        var f = Parse(User("go", "u1"), ToolResult("t1"));

        Assert.Equal("u1", f.LastPromptUuid);
    }

    // Harness-injected user records are not turns at all, and never were — this only
    // confirms the noise filter still runs before the uuid is read.
    [Fact]
    public void HarnessRecordsAreNotTheOperator()
    {
        var f = Parse(
            User("go", "u1"),
            User("<system-reminder>something</system-reminder>", "h1"),
            User("<local-command-stdout>x</local-command-stdout>", "h2"));

        Assert.Equal("u1", f.LastPromptUuid);
    }

    // An interrupt is the operator reaching over and stopping the agent, which is exactly
    // the kind of event a claim about what happens next does not survive.
    [Fact]
    public void AnInterruptCountsAsTheOperator()
    {
        var f = Parse(
            User("go", "u1"),
            Asst("working", "a1"),
            User("[Request interrupted by user]", "u2"));

        Assert.Equal("u2", f.LastPromptUuid);
    }

    [Fact]
    public void NullWhenNothingCarriesOne()
    {
        Assert.Null(Parse(Asst("hello", "a1")).LastPromptUuid);
        Assert.Null(Parse(User("go")).LastPromptUuid);
    }
}
