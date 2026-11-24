using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks what finishes a session by its own word. The convention this exists for — declare
/// state as your last act before stopping — ends the transcript on a dangling tool_use, which
/// the classifier honestly reads as cut-off (see ClassifierTests); the standing claim is the
/// only thing that knows the session is over, so it is what settles it out of
/// <c>list --unfinished</c> and the app's on-the-hook views.
///
/// The edge worth most: a stale claim settles nothing, or "exhausted" from three prompts ago
/// would quietly hide live work.
/// </summary>
public class DeclarationSettlesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Local);

    private static SessionInfo S(
        SessionStatus status = SessionStatus.CutOff,
        string? promptUuid = "u1") => new()
        {
            SessionId = "a",
            Cwd = @"C:\Users\kk\Code\demo",
            Status = status,
            LastPromptUuid = promptUuid,
            LastActive = Now,
            Project = "demo",
            HasOperator = true,
        };

    private static Declaration D(Declared state, string? atTurn = "u1") => new()
        {
            State = state,
            AtTurn = atTurn,
            At = Now,
        };

    // The headline: a session whose last act was `state exhausted --self` reads cut-off
    // forever, and the claim is what finishes it anyway.
    [Theory]
    [InlineData(Declared.Exhausted)]
    [InlineData(Declared.NeedsRead)]
    public void AStandingItIsOverClaimSettles(Declared state)
    {
        Assert.True(D(state).Settles(S()));
    }

    // Both say work remains, so neither may hide the session.
    [Theory]
    [InlineData(Declared.Runnable)]
    [InlineData(Declared.Blocked)]
    [InlineData(Declared.None)]
    public void AClaimThatWorkRemainsNeverSettles(Declared state)
    {
        Assert.False(D(state).Settles(S()));
    }

    [Fact]
    public void AStaleClaimSettlesNothing()
    {
        Assert.False(D(Declared.Exhausted, atTurn: "u1").Settles(S(promptUuid: "u2")));
    }

    // Same rule as StillStands: unverifiable is the same as stale.
    [Fact]
    public void AnUnanchoredClaimOverAFileWithPromptsSettlesNothing()
    {
        Assert.False(D(Declared.Exhausted, atTurn: null).Settles(S(promptUuid: "u1")));
    }

    // A -p run has nothing that could have moved, so a claim written against nothing stands.
    [Fact]
    public void NoPromptEitherSideStillSettles()
    {
        Assert.True(D(Declared.Exhausted, atTurn: null).Settles(S(promptUuid: null)));
    }

    // The status does not matter: settling by declaration never reads the classifier,
    // exactly as a Done mark never does (law 1 — Status itself is untouched).
    [Theory]
    [InlineData(SessionStatus.WaitingYou)]
    [InlineData(SessionStatus.CutOff)]
    [InlineData(SessionStatus.Error)]
    public void SettlingIgnoresWhatTheFileEndedOn(SessionStatus status)
    {
        var session = S(status);
        Assert.True(D(Declared.Exhausted).Settles(session));
        Assert.Equal(status, session.Status);
    }
}
