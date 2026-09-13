using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the one field that says whether anybody was ever here.
///
/// Read <c>origin: {"kind": "human"}</c> and nothing else. The tempting alternatives were
/// measured against 730 session files on 2026-09-13 and both are wrong: <c>entrypoint</c>
/// names the door rather than who came through it — a person driving an SDK harness reports
/// <c>sdk-cli</c>, exactly what a library call reports — and <c>promptSource</c> says
/// <c>typed</c> only for a terminal, which would call the phone and the desktop app
/// unattended. These tests are what keeps someone from "simplifying" to either of them.
/// </summary>
public class HasOperatorTests
{
    private static SessionFileFields Parse(params string[] lines) =>
        SessionFileParser.Parse(lines);

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <param name="extra">Raw JSON properties spliced in, for the fields under test.</param>
    private static string User(string text, string extra = "") =>
        "{\"type\":\"user\"" + extra + ",\"message\":{\"content\":" + J(text) + "}}";

    private const string Human = ",\"origin\":{\"kind\":\"human\"}";
    private const string Notification = ",\"origin\":{\"kind\":\"task-notification\"}";

    [Fact]
    public void AHumanOriginMeansSomebodyWasHere()
    {
        Assert.True(Parse(User("do the thing", Human)).HasOperator);
    }

    [Fact]
    public void NoOriginAtAllMeansNobodyWas()
    {
        // What a library call leaves behind: a prompt, an answer, and no person.
        Assert.False(Parse(
            User("Reply with ONLY strict JSON, no markdown fence: {\"ok\": true}"),
            "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5\","
            + "\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}]}}")
            .HasOperator);
    }

    [Fact]
    public void ATaskNotificationIsAnOriginButNotAnOperator()
    {
        // The harness telling a session its background work finished. It is the other kind
        // seen in the wild, and reading "has an origin" instead of "origin is human" would
        // count it as a person.
        Assert.False(Parse(User("<task-notification>done</task-notification>", Notification)).HasOperator);
    }

    [Fact]
    public void OneHumanRecordAnywhereIsEnough()
    {
        // A session that ran unattended for a while and then had somebody step in — and the
        // reverse, which is the common case: a person starts it and the agent works on.
        Assert.True(Parse(
            User("kicked off by a script"),
            User("actually, stop", Human),
            User("<task-notification>done</task-notification>", Notification)).HasOperator);
    }

    [Fact]
    public void AMetaRecordDoesNotCount()
    {
        Assert.False(Parse(User("restored from a snapshot", ",\"isMeta\":true" + Human)).HasOperator);
    }

    [Fact]
    public void TheHarnessFieldsAreNotConsulted()
    {
        // sdk-cli + promptSource:sdk is what a library call looks like AND what an operator
        // working through the desktop app or a phone looks like. Only the origin separates
        // them, so these two must come out differently despite matching on both other fields.
        const string harness = ",\"entrypoint\":\"sdk-cli\",\"promptSource\":\"sdk\"";

        Assert.True(Parse(User("what state are we in now", harness + Human)).HasOperator);
        Assert.False(Parse(User("Score this page 0-100.", harness)).HasOperator);
    }

    [Fact]
    public void AnEmptyFileHasNoOperator()
    {
        Assert.False(Parse().HasOperator);
    }
}
