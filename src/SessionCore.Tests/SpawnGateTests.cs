using System.Text.Json;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the spawn gate's lines. The widget shape is the status line input Claude Code wrote
/// to ~/.claude/widget-data.json on 2026-11-09.
/// </summary>
public class SpawnGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);
    private static readonly long Later = Now.AddHours(3).ToUnixTimeSeconds();
    private static readonly long Earlier = Now.AddHours(-1).ToUnixTimeSeconds();

    private static string Widget(int fiveHour, int weekly, long? fiveResets = null, long? weekResets = null) =>
        "{\"model\":{\"id\":\"claude-opus-5-5\"},\"rate_limits\":{"
        + $"\"five_hour\":{{\"used_percentage\":{fiveHour},\"resets_at\":{fiveResets ?? Later}}},"
        + $"\"seven_day\":{{\"used_percentage\":{weekly},\"resets_at\":{weekResets ?? Later}}}}}}}";

    private static string Hook(string tool) =>
        "{\"session_id\":\"s\",\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"" + tool
        + "\",\"tool_input\":{\"description\":\"x\",\"prompt\":\"y\"}}";

    private static string? Answer(string tool, string? widget) =>
        SpawnGate.Answer(Hook(tool), widget, Now, TimeZoneInfo.Utc);

    private static JsonElement Out(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("hookSpecificOutput");

    [Fact]
    public void Silent_below_the_warn_line()
    {
        Assert.Null(Answer("Agent", Widget(40, 84)));
    }

    [Fact]
    public void Warns_without_deciding_from_85()
    {
        var a = Answer("Agent", Widget(10, 88));
        Assert.NotNull(a);
        var o = Out(a!);
        Assert.False(o.TryGetProperty("permissionDecision", out _)); // never "allow": that would skip permission prompts
        Assert.Contains("weekly usage limit is at 88%", o.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void Refuses_from_95_on_the_worse_window()
    {
        var o = Out(Answer("Workflow", Widget(97, 20))!);
        Assert.Equal("deny", o.GetProperty("permissionDecision").GetString());
        Assert.Contains("5-hour usage limit is at 97%", o.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void A_window_past_its_reset_counts_as_empty()
    {
        Assert.Null(Answer("Agent", Widget(99, 30, fiveResets: Earlier)));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Read")]
    public void Other_tools_pass_untouched(string tool)
    {
        Assert.Null(Answer(tool, Widget(100, 100)));
    }

    [Fact]
    public void Task_is_the_older_name_for_Agent()
    {
        Assert.NotNull(Answer("Task", Widget(100, 100)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"model\":{}}")]
    public void No_usage_figures_means_no_gate(string? widget)
    {
        Assert.Null(Answer("Agent", widget));
    }

    [Fact]
    public void A_malformed_hook_payload_says_nothing()
    {
        Assert.Null(SpawnGate.Answer("{", Widget(100, 100), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Lines_move_with_the_flags()
    {
        Assert.Null(SpawnGate.Answer(Hook("Agent"), Widget(0, 90), Now, TimeZoneInfo.Utc, warn: 92, deny: 98));
        var o = Out(SpawnGate.Answer(Hook("Agent"), Widget(0, 90), Now, TimeZoneInfo.Utc, warn: 50, deny: 90)!);
        Assert.Equal("deny", o.GetProperty("permissionDecision").GetString());
    }
}
