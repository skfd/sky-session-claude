using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the rule for restarting a live session on the operator's behalf. The cost of a
/// wrong "safe" is someone's half-typed reply or a turn thrown away mid-flight, so the
/// bar is proof that nothing is lost — not an absence of evidence that something is.
/// </summary>
public class RestartPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Local);

    private static LiveSession Live(
        string? status = "idle",
        string entrypoint = "cli",
        string kind = "interactive",
        TimeSpan? idleFor = null,
        string? bridge = null,
        string? name = null,
        string? nameSource = null,
        string? cwd = @"C:\Users\kk\Code\demo") => new()
        {
            Pid = 4242,
            SessionId = "b9e83ad3-8742-4f86-b5e3-40e844f24da1",
            Cwd = cwd,
            Version = "2.1.239",
            Status = status,
            StatusUpdatedAt = Now - (idleFor ?? TimeSpan.FromMinutes(10)),
            BridgeSessionId = bridge,
            Name = name,
            NameSource = nameSource,
            Kind = kind,
            Entrypoint = entrypoint,
        };

    // --- the sweepable set --------------------------------------------------

    [Fact]
    public void IdleAndSettledAndFinished_IsSafe()
    {
        var verdict = RestartPolicy.Judge(Live(), SessionStatus.Complete, Now);
        Assert.Equal(RestartSafety.Safe, verdict.Safety);
        Assert.True(verdict.CanSweep);
    }

    /// <summary>An error or a rate limit is exactly what you restart out of.</summary>
    [Theory]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Limit)]
    [InlineData(SessionStatus.Interrupted)]
    public void IdleAfterAnErrorOrLimit_IsStillSafe(SessionStatus tail) =>
        Assert.Equal(RestartSafety.Safe, RestartPolicy.Judge(Live(), tail, Now).Safety);

    /// <summary>The scanner may not have read the file; idle and settled still stands on its own.</summary>
    [Fact]
    public void NoTailStatusAtAll_StillJudgesFromTheRegistry() =>
        Assert.Equal(RestartSafety.Safe, RestartPolicy.Judge(Live(), null, Now).Safety);

    // --- in flight ----------------------------------------------------------

    [Fact]
    public void Busy_IsUnsafe()
    {
        var verdict = RestartPolicy.Judge(Live(status: "busy"), SessionStatus.Complete, Now);
        Assert.Equal(RestartSafety.Unsafe, verdict.Safety);
        Assert.Contains("in flight", verdict.Reason);
    }

    /// <summary>
    /// The desktop app and the SDK publish no status, and we own no terminal of theirs.
    /// Silence is not consent.
    /// </summary>
    [Fact]
    public void NoPublishedStatus_IsUnsafe() =>
        Assert.Equal(RestartSafety.Unsafe, RestartPolicy.Judge(Live(status: null), SessionStatus.Complete, Now).Safety);

    [Theory]
    [InlineData("claude-desktop")]
    [InlineData("sdk-cli")]
    public void SessionsWeDoNotHostAreUnsafe(string entrypoint)
    {
        var verdict = RestartPolicy.Judge(Live(entrypoint: entrypoint), SessionStatus.Complete, Now);
        Assert.Equal(RestartSafety.Unsafe, verdict.Safety);
    }

    /// <summary>
    /// "idle" only means no turn is running. A tool step that never came back leaves the
    /// process at rest with the work half-done, and dropping that work is the operator's call.
    /// </summary>
    [Theory]
    [InlineData(SessionStatus.WaitingAgent)]
    [InlineData(SessionStatus.CutOff)]
    public void IdleButStoppedMidStep_IsUnsafe(SessionStatus tail)
    {
        var verdict = RestartPolicy.Judge(Live(), tail, Now);
        Assert.Equal(RestartSafety.Unsafe, verdict.Safety);
        Assert.Contains("mid-step", verdict.Reason);
    }

    [Fact]
    public void AnUnknownStateIsNeverAssumedIdle() =>
        Assert.Equal(RestartSafety.Unsafe, RestartPolicy.Judge(Live(status: "reconnecting"), null, Now).Safety);

    // --- offered, never swept ----------------------------------------------

    /// <summary>The one thing no file can see: text typed into the box and not sent.</summary>
    [Fact]
    public void AQuestionWaitingOnYou_IsOfferedNotSwept()
    {
        var verdict = RestartPolicy.Judge(Live(), SessionStatus.WaitingYou, Now);
        Assert.Equal(RestartSafety.Ask, verdict.Safety);
        Assert.False(verdict.CanSweep);
        Assert.Contains("not sent", verdict.Reason);
    }

    [Fact]
    public void JustWentIdle_IsOfferedNotSwept()
    {
        var verdict = RestartPolicy.Judge(Live(idleFor: TimeSpan.FromSeconds(5)), SessionStatus.Complete, Now);
        Assert.Equal(RestartSafety.Ask, verdict.Safety);
        Assert.Contains("still be typing", verdict.Reason);
    }

    /// <summary>A turn in flight outranks "you may be typing" — the reason has to be the real one.</summary>
    [Fact]
    public void BusyOutranksTheSettleWindow() =>
        Assert.Contains("in flight",
            RestartPolicy.Judge(Live(status: "busy", idleFor: TimeSpan.FromSeconds(1)), null, Now).Reason);

    // --- the command that brings it back ------------------------------------

    [Fact]
    public void PlainSessionResumesPlainly() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1",
            RestartPolicy.ResumeCommand(Live()));

    /// <summary>
    /// Remote Control is per-session and dies with the process, so a restart that forgets to
    /// ask for it again silently drops the session off the operator's phone.
    /// </summary>
    [Fact]
    public void RemoteControlIsRequestedAgainOnTheWayBackUp() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --remote-control",
            RestartPolicy.ResumeCommand(Live(bridge: "session_01NpwuF1HVr5CRthp5YS8SWH")));

    [Fact]
    public void ANameYouChoseSurvivesTheRestart() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --remote-control 'night shift'",
            RestartPolicy.ResumeCommand(
                Live(bridge: "session_x", name: "night shift", nameSource: "custom")));

    /// <summary>A derived name is the CLI's own slug; pinning it would freeze a stale label.</summary>
    [Fact]
    public void ADerivedNameIsLeftToReDerive() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --remote-control",
            RestartPolicy.ResumeCommand(
                Live(bridge: "session_x", name: "demo-6c", nameSource: "derived")));

    [Fact]
    public void RelaunchReturnsTheShellToTheSessionFolder() =>
        Assert.Equal(@"cd 'C:\Users\kk\Code\demo'; claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1",
            RestartPolicy.RelaunchLine(Live()));

    [Fact]
    public void AQuoteInAPathCannotBreakOutOfTheCommand()
    {
        var line = RestartPolicy.RelaunchLine(Live(cwd: @"C:\it's\here"));
        Assert.Equal(@"cd 'C:\it''s\here'; claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1", line);
    }

    [Fact]
    public void NoRecordedFolderMeansJustResumeWhereTheShellStands() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1",
            RestartPolicy.RelaunchLine(Live(cwd: null)));
}
