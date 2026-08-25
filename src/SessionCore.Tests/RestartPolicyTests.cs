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
        Assert.Equal(SweepSafety.Safe, verdict.Safety);
        Assert.True(verdict.CanSweep);
    }

    /// <summary>An error or a rate limit is exactly what you restart out of.</summary>
    [Theory]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Limit)]
    [InlineData(SessionStatus.Interrupted)]
    public void IdleAfterAnErrorOrLimit_IsStillSafe(SessionStatus tail) =>
        Assert.Equal(SweepSafety.Safe, RestartPolicy.Judge(Live(), tail, Now).Safety);

    /// <summary>The scanner may not have read the file; idle and settled still stands on its own.</summary>
    [Fact]
    public void NoTailStatusAtAll_StillJudgesFromTheRegistry() =>
        Assert.Equal(SweepSafety.Safe, RestartPolicy.Judge(Live(), null, Now).Safety);

    // --- in flight ----------------------------------------------------------

    [Fact]
    public void Busy_IsUnsafe()
    {
        var verdict = RestartPolicy.Judge(Live(status: "busy"), SessionStatus.Complete, Now);
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("in flight", verdict.Reason);
    }

    /// <summary>
    /// The desktop app and the SDK publish no status, and we own no terminal of theirs.
    /// Silence is not consent.
    /// </summary>
    [Fact]
    public void NoPublishedStatus_IsUnsafe() =>
        Assert.Equal(SweepSafety.Unsafe, RestartPolicy.Judge(Live(status: null), SessionStatus.Complete, Now).Safety);

    [Theory]
    [InlineData("claude-desktop")]
    [InlineData("sdk-cli")]
    public void SessionsWeDoNotHostAreUnsafe(string entrypoint)
    {
        var verdict = RestartPolicy.Judge(Live(entrypoint: entrypoint), SessionStatus.Complete, Now);
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
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
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("mid-step", verdict.Reason);
    }

    /// <summary>
    /// The CLI publishes a third state beside busy and idle: "waiting", meaning it is
    /// blocked on the operator — a permission prompt, or a question mid-turn. Nothing is
    /// running, so a restart would work; it would just throw away whatever was waiting to
    /// be approved. That is a decision to offer, not one to make for someone.
    /// </summary>
    [Fact]
    public void WaitingOnAnAnswer_IsOfferedNotSwept()
    {
        var verdict = RestartPolicy.Judge(Live(status: "waiting"), SessionStatus.CutOff, Now);
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.False(verdict.CanSweep);
        Assert.Contains("pending approval", verdict.Reason);
    }

    [Fact]
    public void AnUnknownStateIsNeverAssumedIdle() =>
        Assert.Equal(SweepSafety.Unsafe, RestartPolicy.Judge(Live(status: "reconnecting"), null, Now).Safety);

    // --- offered, never swept ----------------------------------------------

    /// <summary>The one thing no file can see: text typed into the box and not sent.</summary>
    [Fact]
    public void AQuestionWaitingOnYou_IsOfferedNotSwept()
    {
        var verdict = RestartPolicy.Judge(Live(), SessionStatus.WaitingYou, Now);
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.False(verdict.CanSweep);
        Assert.Contains("not sent", verdict.Reason);
    }

    [Fact]
    public void JustWentIdle_IsOfferedNotSwept()
    {
        var verdict = RestartPolicy.Judge(Live(idleFor: TimeSpan.FromSeconds(5)), SessionStatus.Complete, Now);
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.Contains("still be typing", verdict.Reason);
    }

    /// <summary>A turn in flight outranks "you may be typing" — the reason has to be the real one.</summary>
    [Fact]
    public void BusyOutranksTheSettleWindow() =>
        Assert.Contains("in flight",
            RestartPolicy.Judge(Live(status: "busy", idleFor: TimeSpan.FromSeconds(1)), null, Now).Reason);

    // --- the command that brings it back ------------------------------------

    /// <summary>
    /// Without Remote Control the name still goes in, under --name: the point is that the
    /// session comes back answering to the same thing, not that it is on a phone.
    /// </summary>
    [Fact]
    public void ASessionWithNoTitleIsNamedForItsFolderAndId() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'demo-b9'",
            RestartPolicy.ResumeCommand(Live()));

    /// <summary>
    /// Remote Control is per-session and dies with the process, so a restart that forgets to
    /// ask for it again silently drops the session off the operator's phone.
    /// </summary>
    [Fact]
    public void RemoteControlIsRequestedAgainOnTheWayBackUp() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'demo-b9' --remote-control",
            RestartPolicy.ResumeCommand(Live(bridge: "session_01NpwuF1HVr5CRthp5YS8SWH")));

    /// <summary>
    /// Whatever name it is handed is the name that goes on the line. Working out which name
    /// that should be used to happen here, which made this a second decider that knew nothing
    /// about which names were Sky's own -- so a restart re-froze the last placeholder every
    /// time. That decision is NamePolicy's now, and this stays pure.
    /// </summary>
    [Fact]
    public void TheNameItIsHandedIsTheNameItWrites() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'night shift' --remote-control",
            RestartPolicy.ResumeCommand(
                Live(bridge: "session_x", name: "demo-6c", nameSource: "derived"), "night shift"));

    /// <summary>
    /// Including over the name the session currently answers to. Deciding not to rename it is
    /// something the caller expresses by handing back the name it already has, not by handing
    /// back nothing.
    /// </summary>
    [Fact]
    public void ANameItIsHandedOverridesTheOneItHas() =>
        Assert.Equal(
            "claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'Add retry logic to address-vault download'",
            RestartPolicy.ResumeCommand(
                Live(name: "night shift", nameSource: null),
                "Add retry logic to address-vault download"));

    /// <summary>
    /// A command line has to say something, so no name falls to the floor rather than to
    /// nothing -- leaving it off would let the CLI re-derive one with a fresh suffix, which is
    /// the churn this whole path exists to stop.
    /// </summary>
    [Fact]
    public void NoNameFallsToTheFloor() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'demo-b9'",
            RestartPolicy.ResumeCommand(Live(name: "demo-6c", nameSource: "derived")));

    [Fact]
    public void RelaunchReturnsTheShellToTheSessionFolder() =>
        Assert.Equal(
            @"cd 'C:\Users\kk\Code\demo'; claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'demo-b9'",
            RestartPolicy.RelaunchLine(Live()));

    [Fact]
    public void AQuoteInAPathCannotBreakOutOfTheCommand()
    {
        var line = RestartPolicy.RelaunchLine(Live(cwd: @"C:\it's\here"));
        Assert.StartsWith(@"cd 'C:\it''s\here'; claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1", line);
    }

    /// <summary>A name is typed into a shell like anything else, so it is quoted like one.</summary>
    [Fact]
    public void AQuoteInANameCannotBreakOutOfTheCommand() =>
        Assert.EndsWith("--name 'it''s working'",
            RestartPolicy.ResumeCommand(Live(), "it's working"));

    [Fact]
    public void NoRecordedFolderMeansJustResumeWhereTheShellStands() =>
        Assert.Equal("claude --resume b9e83ad3-8742-4f86-b5e3-40e844f24da1 --name 'session-b9'",
            RestartPolicy.RelaunchLine(Live(cwd: null)));
}
