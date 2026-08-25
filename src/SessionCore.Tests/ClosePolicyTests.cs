using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the rule for closing a live session at the end of the day.
///
/// Close inherits every refusal a restart makes — those tests live next door — so what is
/// pinned here is the part only close has to decide: a session may look perfectly quiet and
/// still be one you want to find open in the morning.
/// </summary>
public class ClosePolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 18, 0, 0, DateTimeKind.Local);

    private static LiveSession Live(
        string? status = "idle",
        string entrypoint = "cli",
        string kind = "interactive",
        TimeSpan? idleFor = null) => new()
        {
            Pid = 4242,
            SessionId = "b9e83ad3-8742-4f86-b5e3-40e844f24da1",
            Cwd = @"C:\Users\kk\Code\demo",
            Version = "2.1.239",
            Status = status,
            StatusUpdatedAt = Now - (idleFor ?? TimeSpan.FromMinutes(10)),
            Kind = kind,
            Entrypoint = entrypoint,
        };

    private static SweepVerdict Judge(
        SessionStatus? tail, Disposition mark = Disposition.None, LiveSession? live = null) =>
        ClosePolicy.Judge(live ?? Live(), tail, mark, Now);

    // --- the sweepable set --------------------------------------------------

    [Fact]
    public void FinishedAndIdle_IsSafe()
    {
        var verdict = Judge(SessionStatus.Complete);
        Assert.Equal(SweepSafety.Safe, verdict.Safety);
        Assert.True(verdict.CanSweep);
    }

    /// <summary>Your tick is the whole point: it settles what the classifier could not.</summary>
    [Theory]
    [InlineData(Disposition.Done)]
    [InlineData(Disposition.Abandoned)]
    public void MarkedOff_IsSafeWhateverTheFileEndedOn(Disposition mark)
    {
        Assert.Equal(SweepSafety.Safe, Judge(SessionStatus.Error, mark).Safety);
        Assert.Equal(SweepSafety.Safe, Judge(SessionStatus.WaitingYou, mark).Safety);
        Assert.Equal(SweepSafety.Safe, Judge(SessionStatus.Interrupted, mark).Safety);
    }

    // --- quiet, but not over ------------------------------------------------

    /// <summary>
    /// The line close draws that restart does not. Restarting one of these costs nothing —
    /// it comes straight back. Closing it takes away the only thing telling you tomorrow
    /// that it is not finished.
    /// </summary>
    [Theory]
    [InlineData(SessionStatus.WaitingYou)]
    [InlineData(SessionStatus.Interrupted)]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Limit)]
    public void IdleButUnfinished_IsOnlyOffered(SessionStatus tail)
    {
        var verdict = Judge(tail);
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.False(verdict.CanSweep);
        Assert.Contains(tail.ToWire(), verdict.Reason);
    }

    /// <summary>
    /// Restart is happy to sweep on the registry alone; close is not. "I could not see
    /// whether it was finished" is not "it was finished".
    /// </summary>
    [Fact]
    public void NoSessionFileRead_IsOnlyOffered()
    {
        Assert.Equal(SweepSafety.Ask, Judge(null).Safety);
    }

    // --- what a mark cannot buy ---------------------------------------------

    /// <summary>
    /// A mark answers the file's doubts, never the process's. Ticking a session off says
    /// nothing about the turn still running inside it.
    /// </summary>
    [Fact]
    public void MarkedDone_DoesNotOverrideATurnInFlight()
    {
        var verdict = Judge(SessionStatus.Complete, Disposition.Done, Live(status: "busy"));
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Equal("a turn is in flight", verdict.Reason);
    }

    [Fact]
    public void MarkedDone_DoesNotOverrideAPendingApproval()
    {
        var verdict = Judge(SessionStatus.Complete, Disposition.Done, Live(status: "waiting"));
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.Contains("waiting on an answer", verdict.Reason);
    }

    /// <summary>
    /// The one that would otherwise slip through: a ticked-off session you are sitting in
    /// front of right now is still a session you are sitting in front of.
    /// </summary>
    [Fact]
    public void MarkedDone_DoesNotOverrideAnIdleTooFreshToTrust()
    {
        var verdict = Judge(
            SessionStatus.WaitingYou, Disposition.Done, Live(idleFor: TimeSpan.FromSeconds(5)));
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.Contains("only just went idle", verdict.Reason);
    }

    [Fact]
    public void MarkedAbandoned_DoesNotOverrideStoppedMidStep()
    {
        var verdict = Judge(SessionStatus.WaitingAgent, Disposition.Abandoned);
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("mid-step", verdict.Reason);
    }

    // --- sessions we cannot close at all ------------------------------------

    /// <summary>
    /// No console to send Ctrl-C to. The desktop app and the SDK publish registry entries
    /// like anything else, and there is nothing we can do with them.
    /// </summary>
    [Theory]
    [InlineData("claude-desktop")]
    [InlineData("sdk-cli")]
    public void NotATerminal_IsUnsafe(string entrypoint)
    {
        var verdict = Judge(SessionStatus.Complete, Disposition.Done, Live(entrypoint: entrypoint));
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("not a terminal we can drive", verdict.Reason);
    }

    [Fact]
    public void SubagentSession_IsUnsafe()
    {
        Assert.Equal(
            SweepSafety.Unsafe,
            Judge(SessionStatus.Complete, Disposition.Done, Live(kind: "subagent")).Safety);
    }
}
