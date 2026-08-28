using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the rule for restarting a <c>claude rc</c> host unattended.
///
/// A host has nothing of its own to lose — no conversation, no input box, no draft — so
/// every one of these cases is really about what it is serving. The sweepable host is the
/// one whose conversations are all idle and settled, or which has none at all; anything in
/// flight or waiting one level down makes the host itself untouchable.
/// </summary>
public class HostRestartPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Local);

    private static RemoteControlHost Host(string processName = "claude.exe.old.1787697313311") => new()
    {
        Pointer = new BridgePointer { SessionId = "session_abc123", Pid = 4242 },
        Folder = @"C:\Users\kk\Code\demo",
        ProjectDir = @"C:\Users\kk\.claude\projects\C--Users-kk-Code-demo",
        ProcessName = processName,
        CommandLine = @"""C:\Users\kk\.local\bin\claude.exe"" rc",
    };

    private static HostRestartPolicy.Served Served(
        string? status = "idle",
        TimeSpan? idleFor = null,
        SessionStatus? tail = SessionStatus.Complete) =>
        new(new LiveSession
        {
            Pid = 5150,
            SessionId = "1a4f7c62-0000-4000-8000-000000000001",
            Status = status,
            StatusUpdatedAt = Now - (idleFor ?? TimeSpan.FromMinutes(10)),
            BridgeSessionId = "session_abc123",
            Kind = "interactive",
            Entrypoint = "sdk-cli",
        }, tail);

    private static SweepVerdict Judge(params HostRestartPolicy.Served[] serving) =>
        HostRestartPolicy.Judge(Host(), serving, serving.Length, Now);

    // --- the sweepable set --------------------------------------------------

    /// <summary>The commonest case by far: a host nobody has started a conversation in.</summary>
    [Fact]
    public void ServingNothing_IsSafe()
    {
        var verdict = HostRestartPolicy.Judge(Host(), [], 0, Now);
        Assert.Equal(SweepSafety.Safe, verdict.Safety);
        Assert.Equal("serving nothing", verdict.Reason);
    }

    [Fact]
    public void ServingOnlyIdleSettledConversations_IsSafe() =>
        Assert.Equal(SweepSafety.Safe, Judge(Served(), Served()).Safety);

    /// <summary>An error or a limit one level down is what you restart out of, not away from.</summary>
    [Theory]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Limit)]
    [InlineData(SessionStatus.Interrupted)]
    public void ServingAConversationThatEndedBadly_IsStillSafe(SessionStatus tail) =>
        Assert.Equal(SweepSafety.Safe, Judge(Served(tail: tail)).Safety);

    // --- in flight ----------------------------------------------------------

    [Fact]
    public void ServingABusyConversation_IsUnsafe()
    {
        var verdict = Judge(Served(), Served(status: "busy"));
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("turn in flight", verdict.Reason);
    }

    /// <summary>
    /// The state sdk-cli conversations publish today is none at all, and "we cannot tell"
    /// has to read as unsafe or the whole rule is decoration.
    /// </summary>
    [Fact]
    public void ServingAConversationWithNoPublishedState_IsUnsafe()
    {
        var verdict = Judge(Served(status: null));
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("publishes no busy/idle state", verdict.Reason);
    }

    [Theory]
    [InlineData(SessionStatus.WaitingAgent)]
    [InlineData(SessionStatus.CutOff)]
    public void ServingAConversationStoppedMidStep_IsUnsafe(SessionStatus tail) =>
        Assert.Equal(SweepSafety.Unsafe, Judge(Served(tail: tail)).Safety);

    [Fact]
    public void ServingAConversationInAnUnknownState_IsUnsafe() =>
        Assert.Equal(SweepSafety.Unsafe, Judge(Served(status: "reticulating")).Safety);

    /// <summary>
    /// The gap between the process tree and the registry is a conversation that has spawned
    /// and not yet published anything — the one thing a registry-only sweep would walk into.
    /// </summary>
    [Fact]
    public void MoreChildrenThanRegistryRows_IsUnsafe()
    {
        var verdict = HostRestartPolicy.Judge(Host(), [Served()], childProcesses: 2, Now);
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("just spawned", verdict.Reason);
    }

    /// <summary>Fewer, though, is only a conversation that has exited since the scan.</summary>
    [Fact]
    public void FewerChildrenThanRegistryRows_IsNotHeldAgainstIt() =>
        Assert.Equal(SweepSafety.Safe, HostRestartPolicy.Judge(Host(), [Served()], childProcesses: 0, Now).Safety);

    // --- offered, never swept -----------------------------------------------

    /// <summary>
    /// Waiting is the phone's half-typed draft: the question is on a screen we cannot see,
    /// and a restart drops it. Offered, never taken.
    /// </summary>
    [Fact]
    public void ServingAWaitingConversation_IsAsk()
    {
        var verdict = Judge(Served(status: "waiting"));
        Assert.Equal(SweepSafety.Ask, verdict.Safety);
        Assert.False(verdict.CanSweep);
    }

    [Fact]
    public void ServingAConversationThatOnlyJustWentIdle_IsAsk() =>
        Assert.Equal(SweepSafety.Ask, Judge(Served(idleFor: TimeSpan.FromSeconds(5))).Safety);

    [Fact]
    public void ServingAConversationThatAskedYouSomething_IsAsk() =>
        Assert.Equal(SweepSafety.Ask, Judge(Served(tail: SessionStatus.WaitingYou)).Safety);

    /// <summary>The worst answer among the conversations is the host's, not the first one.</summary>
    [Fact]
    public void TheWorstVerdictAmongConversationsWins()
    {
        var verdict = Judge(Served(), Served(status: "waiting"), Served(status: "busy"));
        Assert.Equal(SweepSafety.Unsafe, verdict.Safety);
        Assert.Contains("turn in flight", verdict.Reason);
    }
}
