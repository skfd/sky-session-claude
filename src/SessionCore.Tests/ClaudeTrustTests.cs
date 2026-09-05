using System.Text.Json;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The folder-trust gate, read and answered in <c>~/.claude.json</c>. The file is the
/// operator's whole Claude Code config, so the tests that matter most here are the ones about
/// what the edit leaves alone.
/// </summary>
public class ClaudeTrustTests
{
    private const string Config = """
        {
          "numStartups": 412,
          "projects": {
            "C:/Users/kk/Code/trusted-repo": {
              "allowedTools": [],
              "hasTrustDialogAccepted": true,
              "history": [
                { "display": "a prompt with a } in it", "pastedContents": {} }
              ]
            },
            "C:/Users/kk/Code/new-repo": {
              "allowedTools": [],
              "hasTrustDialogAccepted": false,
              "exampleCount": 3
            }
          },
          "installMethod": "native"
        }
        """;

    [Fact]
    public void ReadsTheFlagForAFolder()
    {
        Assert.True(ClaudeTrust.ReadTrusted(Config, @"C:\Users\kk\Code\trusted-repo"));
        Assert.False(ClaudeTrust.ReadTrusted(Config, @"C:\Users\kk\Code\new-repo"));
    }

    /// <summary>
    /// A folder the config has never heard of is not a "no" — it is a shrug, and the two are
    /// worth telling apart when the answer decides what a sweep says out loud.
    /// </summary>
    [Fact]
    public void SaysNothingAboutAFolderItHasNotHeardOf()
    {
        Assert.Null(ClaudeTrust.ReadTrusted(Config, @"C:\Users\kk\Code\never-seen"));
        Assert.Null(ClaudeTrust.ReadTrusted("not json at all", @"C:\Users\kk\Code\new-repo"));
    }

    /// <summary>
    /// The config on this machine writes its keys with forward slashes; a folder arrives from
    /// the scanner with backslashes. They are the same folder, and Windows does not care about
    /// case either.
    /// </summary>
    [Fact]
    public void MatchesAFolderHoweverItIsSpelled()
    {
        Assert.True(ClaudeTrust.ReadTrusted(Config, @"c:\users\kk\code\TRUSTED-REPO"));
        Assert.True(ClaudeTrust.ReadTrusted(Config, "C:/Users/kk/Code/trusted-repo/"));
    }

    [Fact]
    public void TurnsTheFlagOn()
    {
        Assert.True(ClaudeTrust.TryGrantIn(Config, @"C:\Users\kk\Code\new-repo", out var after, out var why));
        Assert.Equal("", why);
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Users\kk\Code\new-repo"));
    }

    /// <summary>
    /// The whole reason this is a text edit rather than a round trip: everything that is not
    /// the one boolean comes out byte for byte as it went in.
    /// </summary>
    [Fact]
    public void ChangesNothingElseInTheFile()
    {
        ClaudeTrust.TryGrantIn(Config, @"C:\Users\kk\Code\new-repo", out var after, out _);

        Assert.Equal(Config.Replace(
            "\"hasTrustDialogAccepted\": false", "\"hasTrustDialogAccepted\": true"), after);
        Assert.Contains("\"numStartups\": 412", after);
        Assert.Contains("a prompt with a } in it", after);
    }

    /// <summary>An entry with no flag at all gets one rather than being left as it was.</summary>
    [Fact]
    public void AddsTheFlagWhenTheEntryHasNone()
    {
        const string json = """
            {
              "projects": {
                "C:/Users/kk/Code/bare": {
                  "allowedTools": []
                }
              }
            }
            """;

        Assert.True(ClaudeTrust.TryGrantIn(json, @"C:\Users\kk\Code\bare", out var after, out _));
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Users\kk\Code\bare"));
        Assert.Contains("\"allowedTools\": []", after);
        JsonDocument.Parse(after);   // still JSON, which is the whole safety argument
    }

    [Fact]
    public void AddsTheProjectWhenTheConfigHasNeverHeardOfIt()
    {
        Assert.True(ClaudeTrust.TryGrantIn(Config, @"C:\Users\kk\Code\brand-new", out var after, out _));
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Users\kk\Code\brand-new"));
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Users\kk\Code\trusted-repo"));
        Assert.Contains("\"numStartups\": 412", after);
        JsonDocument.Parse(after);
    }

    [Fact]
    public void AddsTheProjectToAnEmptyProjectsObject()
    {
        Assert.True(ClaudeTrust.TryGrantIn(
            """{ "projects": {} }""", @"C:\Code\first", out var after, out _));
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Code\first"));
        JsonDocument.Parse(after);
    }

    /// <summary>
    /// A path that also appears as a value somewhere — the config is full of histories — must
    /// not be mistaken for the entry that owns it.
    /// </summary>
    [Fact]
    public void IgnoresTheFolderWhereItAppearsAsAValue()
    {
        const string json = """
            {
              "lastFolder": "C:/Users/kk/Code/new-repo",
              "projects": {
                "C:/Users/kk/Code/new-repo": {
                  "hasTrustDialogAccepted": false
                }
              }
            }
            """;

        Assert.True(ClaudeTrust.TryGrantIn(json, @"C:\Users\kk\Code\new-repo", out var after, out _));
        Assert.True(ClaudeTrust.ReadTrusted(after, @"C:\Users\kk\Code\new-repo"));
        Assert.Contains("\"lastFolder\": \"C:/Users/kk/Code/new-repo\"", after);
    }

    /// <summary>A config that cannot be understood is left alone and said so.</summary>
    [Fact]
    public void RefusesToEditWhatItCannotRead()
    {
        Assert.False(ClaudeTrust.TryGrantIn("{ not json", @"C:\Code\x", out var after, out var why));
        Assert.Equal("{ not json", after);
        Assert.Contains("not readable as JSON", why);

        Assert.False(ClaudeTrust.TryGrantIn("""{"a":1}""", @"C:\Code\x", out _, out var why2));
        Assert.Contains("no projects object", why2);
    }
}
