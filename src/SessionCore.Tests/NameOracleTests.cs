using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The parts of the oracle that can be checked without spending a real call on them: what it
/// asks, what it makes of the answer, and the bound on what it is allowed to delete.
/// </summary>
public class NameOracleTests
{
    // --- reading the answer ---------------------------------------------------

    /// <summary>
    /// --output-format json is always on, for the session_id: it is what makes deleting the
    /// transcript the call created safe, because the id was reported rather than inferred.
    /// </summary>
    [Fact]
    public void TheAnswerAndTheTranscriptItMadeAreBothRead()
    {
        var (text, id) = NameOracle.ReadAnswer(
            """{"type":"result","result":"Basemap treatments in Chrome","session_id":"3f9a-…"}""");

        Assert.Equal("Basemap treatments in Chrome", text);
        Assert.Equal("3f9a-…", id);
    }

    /// <summary>
    /// Output we did not ask for may still carry a usable name, but nothing said which
    /// transcript it made — so there is no id, and nothing gets deleted on a guess.
    /// </summary>
    [Fact]
    public void OutputThatIsNotJsonLeavesNothingToDelete()
    {
        var (text, id) = NameOracle.ReadAnswer("Basemap treatments in Chrome\n");

        Assert.Equal("Basemap treatments in Chrome", text);
        Assert.Null(id);
    }

    // --- tidying it -----------------------------------------------------------

    /// <summary>Haiku wraps a title in backticks given half a chance, and quotes it given the other.</summary>
    [Theory]
    [InlineData("`Basemap treatments in Chrome`")]
    [InlineData("\"Basemap treatments in Chrome\"")]
    [InlineData("  Basemap treatments in Chrome.  ")]
    [InlineData("**Basemap treatments in Chrome**")]
    public void TheAnswerIsUnwrapped(string answer) =>
        Assert.Equal("Basemap treatments in Chrome", NameOracle.Clean(answer));

    /// <summary>A model that explained itself anyway put the title first.</summary>
    [Fact]
    public void OnlyTheFirstLineIsTheTitle() =>
        Assert.Equal("Basemap treatments in Chrome",
            NameOracle.Clean("Basemap treatments in Chrome\n\nThis session was mostly about…"));

    [Fact]
    public void ASentenceCaseAnswerIsLeftAsItIs() =>
        Assert.Equal("Basemap treatments in Chrome", NameOracle.Clean("basemap treatments in Chrome"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("``")]
    public void AnEmptyAnswerIsNoAnswer(string answer) => Assert.Null(NameOracle.Clean(answer));

    // --- what it asks ---------------------------------------------------------

    /// <summary>
    /// The drift rule, in the prompt: a session that ends on a commit is not a session about
    /// committing. It is asked for rather than enforced because it needs judgement.
    /// </summary>
    [Fact]
    public void ItAsksForTheLargestThingRatherThanTheLast()
    {
        var prompt = NameOracle.PromptFor(new SessionInfo
        {
            SessionId = "b9e83ad3",
            Cwd = @"C:\Users\kk\Code\vagabond-map",
            Recap = "Pushed the branch.",
        });

        Assert.Contains("largest thing", prompt);
        Assert.Contains("vagabond-map", prompt);
        Assert.Contains("Pushed the branch.", prompt);
    }

    /// <summary>The folder is composed on afterwards, so asking for it in the title too would double it.</summary>
    [Fact]
    public void ItAsksTheModelNotToNameTheFolder() =>
        Assert.Contains("Do not name the folder", NameOracle.PromptFor(
            new SessionInfo { SessionId = "b9e83ad3", Cwd = @"C:\Users\kk\Code\vagabond-map" }));

    // --- the new authority ----------------------------------------------------

    /// <summary>
    /// Deleting from ~/.claude/projects is the one thing this tool does that is not reading,
    /// so it is bounded to an id claude -p reported back — and an id that could be shaped into
    /// a glob or a path is not one of those.
    /// </summary>
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("*")]
    [InlineData("3f9a*")]
    [InlineData(@"C:\Users\kk\file")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyAPlainIdIsEverDeleted(string? id) => Assert.False(NameOracle.CleanUp(id));

    [Fact]
    public void ItDeletesTheTranscriptItsOwnCallMade()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}");
        var project = Path.Combine(dir, "C--Users-kk-scratch");
        Directory.CreateDirectory(project);
        try
        {
            const string id = "3f9a1c22-0821-4d58-87cf-45af838e3698";
            var mine = Path.Combine(project, $"{id}.jsonl");
            var theirs = Path.Combine(project, "b9e83ad3-8742-4f86-b5e3-40e844f24da1.jsonl");
            File.WriteAllText(mine, "{}");
            File.WriteAllText(theirs, "{}");

            Assert.True(NameOracle.CleanUp(id, dir));

            Assert.False(File.Exists(mine));
            Assert.True(File.Exists(theirs));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void CleaningUpWhatIsNotThereIsNotAFailure() =>
        Assert.False(NameOracle.CleanUp("3f9a1c22-0821-4d58-87cf-45af838e3698",
            Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}")));
}
