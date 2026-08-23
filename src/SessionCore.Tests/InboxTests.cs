using SessionCli;

namespace SessionCore.Tests;

/// <summary>
/// The queue file is written by an agent in a sandbox, in one shot, with no way to see an
/// error and correct it before tomorrow. These tests hold both halves of that bargain: the
/// spellings it might reasonably choose are all accepted, and the mistakes that would cost
/// someone a terminal are all refused.
/// </summary>
public class InboxTests
{
    [Fact]
    public void ReadsTheDocumentedShape()
    {
        var parsed = InboxFile.Read("""
            {
              "issuedAt": "2026-08-23T07:12:00+00:00",
              "source": "dispatch",
              "commands": [
                { "action": "resume", "id": "abc1234" },
                { "action": "done", "id": "def5678" }
              ]
            }
            """);

        Assert.Equal("dispatch", parsed.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-08-23T07:12:00+00:00"), parsed.IssuedAt);
        Assert.Equal(["resume", "done"], parsed.Commands.Select(c => c.Action));
        Assert.Equal("abc1234", parsed.Commands[0].Id);
    }

    [Fact]
    public void ReadsABareArray()
    {
        // What you get from a writer thinking in commands rather than in documents. It is
        // unambiguous, so refusing it would only be pedantry with a cost attached.
        var parsed = InboxFile.Read("""[ { "action": "abandon", "id": "abc" } ]""");
        Assert.Equal("abandon", Assert.Single(parsed.Commands).Action);
        Assert.Null(parsed.IssuedAt);
    }

    [Theory]
    [InlineData("""{"commands":[{"verb":"done","session":"abc"}]}""")]
    [InlineData("""{"commands":[{"do":"done","sessionId":"abc"}]}""")]
    [InlineData("""{"actions":[{"action":"DONE","id":"abc"}]}""")]
    public void AcceptsTheSpellingsAWriterMightPick(string json)
    {
        var command = Assert.Single(InboxFile.Read(json).Commands);
        Assert.Equal("done", command.Action);
        Assert.Equal("abc", command.Id);
    }

    [Theory]
    [InlineData("""{"commands":[{"action":"new","in":"C:\\Code\\foo"}]}""")]
    [InlineData("""{"commands":[{"action":"new","folder":"C:\\Code\\foo"}]}""")]
    [InlineData("""{"commands":[{"action":"new","path":"C:\\Code\\foo"}]}""")]
    [InlineData("""{"commands":[{"action":"new","cwd":"C:\\Code\\foo"}]}""")]
    public void AcceptsTheSpellingsOfAFolder(string json)
    {
        Assert.Equal(@"C:\Code\foo", Assert.Single(InboxFile.Read(json).Commands).In);
    }

    [Theory]
    [InlineData("trust")]   // the one answer that must come from the person sitting there
    [InlineData("fork")]    // writes a session file nobody asked to exist
    [InlineData("peek")]
    [InlineData("list")]
    public void RefusesAnActionTheInboxDoesNotRun(string action)
    {
        var e = Assert.Throws<InboxFile.RejectedException>(
            () => InboxFile.Read($$"""{"commands":[{"action":"{{action}}","id":"abc"}]}"""));
        Assert.Contains(action, e.Message);
        Assert.Contains("Allowed:", e.Message);
    }

    [Fact]
    public void RefusesTheWholeFileWhenOneCommandIsUnknown()
    {
        // Not "run the good ones and skip the bad": a file with a verb we do not recognise
        // is a file written against a contract we do not share, and the safe reading of the
        // rest of it is no longer obvious.
        Assert.Throws<InboxFile.RejectedException>(() => InboxFile.Read("""
            {"commands":[{"action":"done","id":"abc"},{"action":"rm -rf","id":"def"}]}
            """));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"commands": "done abc"}""")]
    [InlineData("""{"nothing":"here"}""")]
    [InlineData("""{"commands":[{"id":"abc"}]}""")]
    [InlineData("""{"commands":["done abc"]}""")]
    [InlineData("42")]
    public void RefusesAFileItCannotRunAsWritten(string json)
    {
        Assert.Throws<InboxFile.RejectedException>(() => InboxFile.Read(json));
    }

    [Fact]
    public void LeftoversLandBesideTheQueue()
    {
        var (spent, result) = InboxFile.Paths(@"C:\Code\sky-session-claude\commands.json");
        Assert.Equal(@"C:\Code\sky-session-claude\commands.last.json", spent);
        Assert.Equal(@"C:\Code\sky-session-claude\commands-result.json", result);
    }
}
