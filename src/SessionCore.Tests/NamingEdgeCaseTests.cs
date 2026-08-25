using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The edges the naming work went over on a second pass: values that are not what their type
/// suggests, and cleanup that only happens when nothing goes wrong.
/// </summary>
public class NamingEdgeCaseTests
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";

    // --- a cwd that is not a path ---------------------------------------------

    /// <summary>
    /// <see cref="SessionScanner.BuildRow"/> never leaves Cwd empty — a session file with no
    /// recorded cwd gets a sentence saying so, for the column to show. Read as a path that
    /// sentence slugs into a folder name, and the session ends up called
    /// "unknown-cwd-not-found-in-session-file-b9".
    /// </summary>
    [Fact]
    public void ASessionWithNoRecordedFolderIsNotNamedAfterTheApology()
    {
        var info = new SessionInfo { SessionId = Id, Cwd = SessionInfo.UnknownCwd };

        var inputs = SessionNaming.InputsFor(info, live: null);

        Assert.Null(inputs.Cwd);
        Assert.Equal("session-b9", SessionName.Floor(inputs.SessionId, inputs.Cwd));
    }

    /// <summary>The live registry's folder still wins when there is one; only the apology is dropped.</summary>
    [Fact]
    public void ARealFolderIsStillUsed()
    {
        var info = new SessionInfo { SessionId = Id, Cwd = SessionInfo.UnknownCwd };
        var live = new LiveSession { SessionId = Id, Cwd = @"C:\Users\kk\Code\vagabond-map" };

        Assert.Equal(@"C:\Users\kk\Code\vagabond-map", SessionNaming.InputsFor(info, live).Cwd);
    }

    [Fact]
    public void TheApologyIsNotAFolderAnywhere() =>
        Assert.Equal("", SessionName.RepoOf(SessionInfo.UnknownCwd));

    // --- what the oracle is given ---------------------------------------------

    /// <summary>
    /// Most transcripts carry the operator's turns as `user` records; the `last-prompt`
    /// pointer is often null. Reading only the pointer meant paying two cents for a call that
    /// had been shown almost nothing — the parser has always fallen back to the user records
    /// for exactly this reason.
    /// </summary>
    [Fact]
    public void TheOracleIsShownPromptsFromUserRecordsToo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oracle-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path,
        [
            """{"type":"user","message":{"role":"user","content":"rewrite the tile cache"}}""",
            """{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"ok"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}""",
            """{"type":"user","message":{"role":"user","content":"now make it evict by age"}}""",
        ]);
        try
        {
            var prompt = NameOracle.PromptFor(new SessionInfo { SessionId = Id, FilePath = path });

            Assert.Contains("rewrite the tile cache", prompt);
            Assert.Contains("now make it evict by age", prompt);
            Assert.DoesNotContain("/clear", prompt);        // harness noise, not an ask
            Assert.DoesNotContain("tool_result", prompt);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>A resume rewrites the same pointer, and one ask repeated reads as many asks.</summary>
    [Fact]
    public void TheSameAskRepeatedIsSentOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oracle-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, Enumerable.Repeat(
            """{"type":"last-prompt","lastPrompt":"rewrite the tile cache"}""", 5));
        try
        {
            var prompt = NameOracle.PromptFor(new SessionInfo { SessionId = Id, FilePath = path });

            Assert.Equal(1, prompt.Split("rewrite the tile cache").Length - 1);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
