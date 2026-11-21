using SessionCli;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks what <c>list --unfinished</c> drops — the policy half of <c>Commands.Matches</c>.
/// "Still on the hook" is the question, and three kinds of row are off it without being
/// complete: the abandoned (the operator said no), the declared-over (the session said so,
/// via Settled — see DeclarationSettlesTests), and the unattended (nobody was ever here; a
/// scheduled run reports through its own channel or its parent session). All three stay
/// reachable without the flag, because dropping a row from one view must never mean losing
/// it from the tool.
/// </summary>
public class UnfinishedFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Local);

    private static SessionDto Row(
        SessionStatus status = SessionStatus.Error,
        bool hasOperator = true,
        Disposition disposition = Disposition.None,
        Declaration? claim = null) => SessionDto.From(
            new SessionInfo
            {
                SessionId = "a",
                Cwd = @"C:\Users\kk\Code\demo",
                Status = status,
                LastPromptUuid = "u1",
                LastActive = Now,
                Project = "demo",
                HasOperator = hasOperator,
            },
            disposition,
            claim,
            live: null);

    private static Args A(params string[] argv) => new("list", argv);

    [Fact]
    public void AnUnfinishedSessionSomebodyHadStaysOnTheHook()
    {
        Assert.True(Commands.Matches(Row(), A("--unfinished")));
    }

    // The 2026-11-20 reading: 92 of 129 --unfinished rows were scheduled runs nobody ever
    // spoke in — 91 of them one project's out-of-credits storm — each waiting for an
    // operator who was never there.
    [Fact]
    public void AnUnattendedSessionIsNeverOnTheHook()
    {
        Assert.False(Commands.Matches(Row(hasOperator: false), A("--unfinished")));
    }

    // Dropped from one view, not lost from the tool.
    [Fact]
    public void AnUnattendedSessionStillListsWithoutTheFlag()
    {
        Assert.True(Commands.Matches(Row(hasOperator: false), A()));
    }

    [Fact]
    public void AnUnattendedSessionIsStillReachableByStatus()
    {
        Assert.True(Commands.Matches(Row(hasOperator: false), A("--status", "error")));
    }

    [Fact]
    public void SettledAndAbandonedDropAsTheyAlwaysHave()
    {
        Assert.False(Commands.Matches(Row(SessionStatus.Complete), A("--unfinished")));
        Assert.False(Commands.Matches(
            Row(disposition: Disposition.Abandoned), A("--unfinished")));
    }

    // The other half of this change (see DeclarationSettlesTests): a standing "it is over"
    // claim settles the row, and the filter reads Settled rather than re-deciding.
    [Fact]
    public void ADeclaredOverSessionDropsToo()
    {
        var claim = new Declaration
        {
            State = Declared.Exhausted,
            AtTurn = "u1",
            At = Now,
        };

        Assert.False(Commands.Matches(Row(claim: claim), A("--unfinished")));
        Assert.True(Commands.Matches(Row(claim: claim), A()));
    }
}
