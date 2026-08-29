using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks what a whole project reads as.
///
/// Two things here are worth more than the rest. The first is the order the three sources
/// answer in — operator, then agent, then classifier — because getting it wrong is how a
/// declaration quietly overwrites a fact, or a cross the operator made ends up deciding
/// something. The second is that the classifier is never allowed to call a project
/// <c>blocked</c>: that is the one guess the design forbids, and a test is the only thing
/// that will still be saying so in six months.
/// </summary>
public class ProjectFoldTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Local);

    private static SessionInfo S(
        string id,
        SessionStatus status = SessionStatus.Complete,
        string cwd = @"C:\Users\kk\Code\demo",
        string? promptUuid = "u1",
        int minutesAgo = 0) => new()
        {
            SessionId = id,
            Cwd = cwd,
            Status = status,
            LastPromptUuid = promptUuid,
            LastActive = Now.AddMinutes(-minutesAgo),
            Project = "demo",
        };

    private static Declaration D(Declared state, string? note = null, string? atTurn = "u1") =>
        new() { State = state, Note = note, AtTurn = atTurn, At = Now };

    private static ProjectRoll Roll(
        IEnumerable<SessionInfo> sessions,
        Dictionary<string, Disposition>? marks = null,
        Dictionary<string, Declaration>? claims = null,
        params string[] working) =>
        ProjectFold.Roll(
            sessions,
            id => marks is not null && marks.TryGetValue(id, out var m) ? m : Disposition.None,
            id => claims is not null && claims.TryGetValue(id, out var c) ? c : null,
            working.Contains)
            .Single();

    // --- what the classifier alone says -------------------------------------

    [Fact]
    public void AllSettledIsQuiet()
    {
        Assert.Equal(ProjectState.Quiet, Roll([S("a"), S("b")]).State);
    }

    [Fact]
    public void WaitingAgentIsRunnable()
    {
        Assert.Equal(ProjectState.Runnable, Roll([S("a"), S("b", SessionStatus.WaitingAgent)]).State);
    }

    [Theory]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Limit)]
    [InlineData(SessionStatus.CutOff)]
    public void DiedMidWorkIsBroken(SessionStatus status)
    {
        Assert.Equal(ProjectState.Broken, Roll([S("a", status)]).State);
    }

    // The rule the whole design rests on: "answer, and three more things happen" and "that
    // was the last question" are the same file, so the classifier does not get to pick.
    [Theory]
    [InlineData(SessionStatus.WaitingYou)]
    [InlineData(SessionStatus.Interrupted)]
    public void TheBallBeingYoursIsUndeclaredWithoutADeclaration(SessionStatus status)
    {
        Assert.Equal(ProjectState.Undeclared, Roll([S("a", status)]).State);
    }

    [Fact]
    public void MostUrgentSessionWins()
    {
        var roll = Roll([
            S("quiet"),
            S("run", SessionStatus.WaitingAgent),
            S("dead", SessionStatus.Error),
        ]);

        Assert.Equal(ProjectState.Broken, roll.State);
        Assert.Equal("dead", roll.StateFrom);
    }

    // --- what is happening right now ----------------------------------------

    // A session taking a turn ends its file on a tool_use, which is the same last record a
    // session that died mid-tool leaves. The classifier is right to call both cut-off; the
    // process is the only thing that tells them apart, so it gets the last word here.
    [Fact]
    public void ASessionMidTurnIsNotBroken()
    {
        var roll = Roll([S("a", SessionStatus.CutOff)], working: "a");

        Assert.Equal(ProjectState.Runnable, roll.State);
    }

    [Fact]
    public void ADeadSessionInTheSameProjectStillShows()
    {
        var roll = Roll([S("a", SessionStatus.CutOff), S("b", SessionStatus.Error)], working: "a");

        Assert.Equal(ProjectState.Broken, roll.State);
        Assert.Equal("b", roll.StateFrom);
    }

    // Working is a fact about the process, and the operator's word still outranks it.
    [Fact]
    public void WorkingDoesNotUndoADeclaration()
    {
        var roll = Roll(
            [S("a", SessionStatus.CutOff)],
            claims: new() { ["a"] = D(Declared.Blocked, note: "waiting on the API key") },
            working: "a");

        Assert.Equal(ProjectState.Blocked, roll.State);
    }

    // --- the operator's word ------------------------------------------------

    // The trap the plan names: a crossed-out session must never be what makes a project
    // read undeclared, or every abandoned experiment reads as work you forgot about.
    [Fact]
    public void AbandonedIsSkippedEntirely()
    {
        var roll = Roll(
            [S("a"), S("x", SessionStatus.WaitingYou)],
            marks: new() { ["x"] = Disposition.Abandoned });

        Assert.Equal(ProjectState.Quiet, roll.State);
        Assert.Equal(1, roll.Sessions);
        Assert.Equal(1, roll.Abandoned);
    }

    [Fact]
    public void NothingButCrossesReadsAbandoned()
    {
        var roll = Roll(
            [S("x", SessionStatus.WaitingYou), S("y", SessionStatus.Error)],
            marks: new() { ["x"] = Disposition.Abandoned, ["y"] = Disposition.Abandoned });

        Assert.Equal(ProjectState.Abandoned, roll.State);
        Assert.Equal(0, roll.Sessions);
        Assert.Equal(2, roll.Abandoned);
    }

    [Fact]
    public void DoneFoldsAsSettled()
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou)],
            marks: new() { ["a"] = Disposition.Done });

        Assert.Equal(ProjectState.Quiet, roll.State);
        Assert.Equal(0, roll.Unfinished);
    }

    // The operator outranks the agent: a tick means finished whatever the session claimed.
    [Fact]
    public void DoneBeatsALiveDeclaration()
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou)],
            marks: new() { ["a"] = Disposition.Done },
            claims: new() { ["a"] = D(Declared.Blocked) });

        Assert.Equal(ProjectState.Quiet, roll.State);
    }

    // --- the agent's word ---------------------------------------------------

    [Theory]
    [InlineData(Declared.Runnable, ProjectState.Runnable)]
    [InlineData(Declared.Blocked, ProjectState.Blocked)]
    [InlineData(Declared.NeedsRead, ProjectState.NeedsRead)]
    [InlineData(Declared.Exhausted, ProjectState.Exhausted)]
    public void ADeclarationDecidesTheState(Declared declared, ProjectState expected)
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou)],
            claims: new() { ["a"] = D(declared, note: "the blocker is you") });

        Assert.Equal(expected, roll.State);
        Assert.Equal("the blocker is you", roll.Note);
        Assert.Equal(1, roll.Declared);
    }

    // Most of what this feature is for: the session lands the change and asks "want me to
    // push?", so its Status is waiting-you forever. The declaration moves the project and
    // leaves the session's own Status exactly where the classifier put it (law 1).
    [Fact]
    public void ExhaustedMovesTheProjectAndNotTheSession()
    {
        var session = S("a", SessionStatus.WaitingYou);
        var roll = Roll([session], claims: new() { ["a"] = D(Declared.Exhausted) });

        Assert.Equal(ProjectState.Exhausted, roll.State);
        Assert.Equal(SessionStatus.WaitingYou, session.Status);
    }

    // needs-read is a claim about a session that already looks finished, so the settled
    // check must not get there first.
    [Fact]
    public void NeedsReadSurvivesASessionThatLooksComplete()
    {
        var roll = Roll(
            [S("a")],
            claims: new() { ["a"] = D(Declared.NeedsRead, note: "the audit is in the last message") });

        Assert.Equal(ProjectState.NeedsRead, roll.State);
    }

    // --- expiry -------------------------------------------------------------

    [Fact]
    public void ADeclarationDiesWhenTheOperatorSaysSomethingNew()
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou, promptUuid: "u2")],
            claims: new() { ["a"] = D(Declared.Exhausted, atTurn: "u1") });

        Assert.Equal(ProjectState.Undeclared, roll.State);
        Assert.Equal(0, roll.Declared);
        Assert.Null(roll.Note);
    }

    // A session with no operator prompt at all — a -p run, say — has nothing that could
    // have moved, so a declaration written against nothing still stands.
    [Fact]
    public void NoPromptEitherSideStillStands()
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou, promptUuid: null)],
            claims: new() { ["a"] = D(Declared.Exhausted, atTurn: null) });

        Assert.Equal(ProjectState.Exhausted, roll.State);
    }

    // A claim that never recorded where it was made cannot be checked, and unverifiable
    // is the same as stale.
    [Fact]
    public void AnUnanchoredClaimOverAFileWithPromptsIsStale()
    {
        var roll = Roll(
            [S("a", SessionStatus.WaitingYou, promptUuid: "u1")],
            claims: new() { ["a"] = D(Declared.Exhausted, atTurn: null) });

        Assert.Equal(ProjectState.Undeclared, roll.State);
    }

    // --- grouping -----------------------------------------------------------

    [Fact]
    public void SessionsWithNoFolderJoinNoProject()
    {
        var rolls = ProjectFold.Roll([
            S("a", cwd: SessionInfo.UnknownCwd),
            S("b", cwd: @"C:\Users\kk\Code\demo"),
        ]);

        Assert.Single(rolls);
        Assert.Equal("demo", rolls[0].Project);
    }

    [Fact]
    public void TwoSpellingsOfAFolderAreOneProject()
    {
        var rolls = ProjectFold.Roll([
            S("a", cwd: @"C:\Users\kk\Code\demo"),
            S("b", cwd: @"C:/Users/kk/Code/demo\"),
        ]);

        Assert.Single(rolls);
        Assert.Equal(2, rolls[0].Sessions);
    }

    [Fact]
    public void SessionIdsAreNewestFirstAndIncludeTheCrossedOut()
    {
        var roll = Roll(
            [S("old", minutesAgo: 90), S("new", minutesAgo: 1), S("x", minutesAgo: 30)],
            marks: new() { ["x"] = Disposition.Abandoned });

        Assert.Equal(["new", "x", "old"], roll.SessionIds);
    }

    [Fact]
    public void RollsAreOrderedByUrgency()
    {
        var rolls = ProjectFold.Roll([
            S("a", SessionStatus.Complete, cwd: @"C:\q\quiet"),
            S("b", SessionStatus.WaitingYou, cwd: @"C:\u\undecl"),
            S("c", SessionStatus.Error, cwd: @"C:\b\broken"),
            S("d", SessionStatus.WaitingAgent, cwd: @"C:\r\runnable"),
        ]);

        Assert.Equal(["broken", "runnable", "undecl", "quiet"], rolls.Select(r => r.Project));
    }

    // --- the wire -----------------------------------------------------------

    // Only the four an agent may claim. broken, quiet, undeclared and abandoned are things
    // the fold works out; accepting them would let a session assert a fact.
    [Theory]
    [InlineData("broken")]
    [InlineData("quiet")]
    [InlineData("undeclared")]
    [InlineData("abandoned")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyTheDeclarableStatesParse(string? word)
    {
        Assert.Equal(Declared.None, ProjectFold.FromWire(word));
    }

    [Fact]
    public void EveryDeclarableWordRoundTrips()
    {
        foreach (var word in ProjectFold.Declarable)
        {
            var parsed = ProjectFold.FromWire(word);
            Assert.NotEqual(Declared.None, parsed);
            Assert.Equal(word, ProjectFold.ToWire(parsed));
        }
    }

    [Fact]
    public void EveryProjectStateHasAWireName()
    {
        foreach (var state in Enum.GetValues<ProjectState>())
            Assert.NotEmpty(ProjectFold.ToWire(state));
    }
}
