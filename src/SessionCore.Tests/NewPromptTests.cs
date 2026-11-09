using System.Text;
using SessionCli;

namespace SessionCore.Tests;

/// <summary>
/// `new --prompt` starts a session already working. A prompt is text somebody wrote, and it
/// crosses cmd, sometimes Windows Terminal, then Windows PowerShell 5.1 before claude.exe
/// reads it — every one of which has characters it acts on. Each test here is one of those.
/// </summary>
public class NewPromptTests
{
    private static Args Parse(params string[] argv) => new("new", argv);

    private static string Folder() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "sky-new-prompt-" + Guid.NewGuid().ToString("N"))).FullName;

    // --remote-control takes an optional name, so a prompt after it would become the name.
    [Fact]
    public void PutsThePromptAheadOfEveryFlag()
    {
        Assert.Equal(
            @"cd 'C:\Code\sky'; claude 'tidy the readme' --name 'docs' --chrome --remote-control",
            Commands.NewSessionLine(@"C:\Code\sky", "docs", "tidy the readme"));
    }

    [Fact]
    public void LeavesTheLineAloneWithoutAPrompt()
    {
        Assert.Equal(@"cd 'C:\Code\sky'; claude --chrome --remote-control", Commands.NewSessionLine(@"C:\Code\sky", null, null));
    }

    [Fact]
    public void DoublesApostrophesStraightAndCurly()
    {
        Assert.Equal("'don''t and don\u2019\u2019t'", ClaudeLaunch.PromptArgument("don't and don\u2019t"));
    }

    // Windows PowerShell 5.1 wraps a native argument with a space in quotes and escapes
    // nothing inside, so the prompt's own quotes have to arrive pre-escaped.
    [Fact]
    public void EscapesDoubleQuotesForTheNativeHop()
    {
        Assert.Equal(@"'say \""hi\"" please'", ClaudeLaunch.PromptArgument(@"say ""hi"" please"));
    }

    [Fact]
    public void DoublesBackslashesThatPrecedeAQuote()
    {
        Assert.Equal(@"'path C:\\\""x'", ClaudeLaunch.PromptArgument(@"path C:\""x"));
    }

    // A closing quote follows the argument, so trailing backslashes would escape it.
    [Fact]
    public void DoublesTrailingBackslashes()
    {
        Assert.Equal(@"'look in C:\Code\\'", ClaudeLaunch.PromptArgument(@"look in C:\Code\"));
    }

    // With no space PowerShell adds no quotes, so a trailing backslash is literal as it is.
    [Fact]
    public void LeavesATrailingBackslashAloneWhenNothingWillBeQuoted()
    {
        Assert.Equal(@"'C:\Code\'", ClaudeLaunch.PromptArgument(@"C:\Code\"));
    }

    [Fact]
    public void KeepsAPlainCommandReadable()
    {
        var line = TerminalLauncher.WindowArguments(Commands.NewSessionLine(@"C:\Code\sky", null, "tidy the readme"));
        Assert.Contains("-Command \"cd 'C:\\Code\\sky'; claude 'tidy the readme'", line);
    }

    // cmd expands %VAR% inside quotes and acts on & after a stray quote; -EncodedCommand is
    // base64 and carries the command past it untouched.
    [Theory]
    [InlineData("print %PATH% for me")]
    [InlineData("a \"quoted\" word & then more")]
    [InlineData("first line\nsecond line")]
    [InlineData("pipes | and > and ^")]
    public void EncodesACommandCmdWouldActOn(string prompt)
    {
        var command = Commands.NewSessionLine(@"C:\Code\sky", null, prompt);
        var line = TerminalLauncher.WindowArguments(command);

        Assert.DoesNotContain(prompt, line);
        Assert.Equal(command, Decode(line));
    }

    // Windows Terminal splits a tab at every ;, so a tab encodes where a window need not.
    [Fact]
    public void EncodesASemicolonOnlyForATab()
    {
        const string prompt = "one; two";
        Assert.Contains("-Command", TerminalLauncher.WindowArguments(Commands.NewSessionLine(@"C:\Code\sky", null, prompt)));

        var tab = new TerminalTab(@"C:\Code\sky", null, ClaudeLaunch.New(null, prompt));
        var line = TerminalLauncher.TabArguments([tab], "review");
        Assert.Equal(ClaudeLaunch.New(null, prompt), Decode(line));
        Assert.Single(Occurrences(line, "new-tab"));
    }

    // The app finds a session's tab by the title the CLI paints; a pinned title would hide it.
    [Fact]
    public void LeavesASessionTabUntitled()
    {
        var line = TerminalLauncher.TabArguments([new TerminalTab(@"C:\Code\sky", null, "claude")], "review");
        Assert.DoesNotContain("--title", line);
        Assert.Contains("-w \"review\" new-tab -d \"C:\\Code\\sky\"", line);
    }

    [Fact]
    public void RefusesAPromptTooLongForACommandLine()
    {
        var e = Assert.Throws<UsageException>(() =>
            Commands.New(Parse("--in", Folder(), "--prompt", new string('x', 3000) + " & end", "--dry-run")));
        Assert.Contains("too long", e.Message);
    }

    [Fact]
    public void RefusesBothPromptForms()
    {
        Assert.Throws<UsageException>(() =>
            Commands.New(Parse("--in", Folder(), "--prompt", "a", "--prompt-file", "b.txt", "--dry-run")));
    }

    [Fact]
    public void RefusesAnEmptyPrompt()
    {
        Assert.Throws<UsageException>(() => Commands.New(Parse("--in", Folder(), "--prompt", "   ", "--dry-run")));
    }

    [Fact]
    public void ReadsAPromptFileTrimmed()
    {
        var folder = Folder();
        var file = Path.Combine(folder, "brief.txt");
        File.WriteAllText(file, "pick up the brief\n", new UTF8Encoding(false));

        Assert.Equal(0, Commands.New(Parse("--in", folder, "--prompt-file", file, "--dry-run")));
    }

    // Found by the pid that was not there before and the folder, never the name.
    [Fact]
    public void FindsTheSessionThatStartedSinceTheSnapshot()
    {
        var dir = Folder();
        File.WriteAllText(Path.Combine(dir, "1.json"), """{"pid":1,"sessionId":"old","cwd":"C:\\Code\\sky"}""");
        var before = LiveSessionRegistry.Pids(dir);

        File.WriteAllText(Path.Combine(dir, "2.json"), """{"pid":2,"sessionId":"elsewhere","cwd":"C:\\Code\\other"}""");
        File.WriteAllText(Path.Combine(dir, "3.json"), """{"pid":3,"sessionId":"ours","cwd":"c:\\code\\SKY\\","name":"sky-6f"}""");

        var started = LiveSessionRegistry.StartedSince(before, @"C:\Code\sky", dir);
        Assert.Equal(["ours"], started.Select(s => s.SessionId));
    }

    private static string Decode(string line)
    {
        const string flag = "-EncodedCommand ";
        var at = line.IndexOf(flag, StringComparison.Ordinal);
        Assert.True(at >= 0, "expected an encoded command in: " + line);
        var b64 = line[(at + flag.Length)..].Split(' ')[0];
        return Encoding.Unicode.GetString(Convert.FromBase64String(b64));
    }

    private static List<int> Occurrences(string haystack, string needle)
    {
        var found = new List<int>();
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            found.Add(i);
        return found;
    }
}
