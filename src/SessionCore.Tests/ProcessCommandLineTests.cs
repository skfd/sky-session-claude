using System.Diagnostics;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the second source of truth about who is running a session.
///
/// The registry answers this cheaply and is wrong in exactly one case — a CLI that started
/// and hung before writing its entry. That case is the whole reason force-resume exists, so
/// the matching rule here has to be right about it: too loose and the verb kills a process
/// running some other session, too tight and the stranded one stays invisible.
/// </summary>
public class ProcessCommandLineTests
{
    private const string Id = "344df963-2c2a-474f-b145-6ca03781d3aa";
    private const string Other = "344df963-2c2a-474f-b145-6ca03781d3ab";

    [Fact]
    public void AResumedSessionIsMatchedOnItsId() =>
        Assert.True(ProcessCommandLine.Mentions($@"""C:\Users\kk\.local\bin\claude.exe"" --resume {Id}", Id));

    [Fact]
    public void TheFlagsAroundItDoNotMatter() =>
        Assert.True(ProcessCommandLine.Mentions(
            $@"claude.exe --resume {Id} --name ""Comentality content prototypes"" --remote-control", Id));

    /// <summary>
    /// Ids differ in their last character as readily as their first. Matching on a substring
    /// would let force-resume end a neighbouring session's process, which is the one mistake
    /// here that costs someone work.
    /// </summary>
    [Fact]
    public void ANearlyIdenticalIdIsNotAMatch() =>
        Assert.False(ProcessCommandLine.Mentions($"claude --resume {Other}", Id));

    /// <summary>Ids take any unique prefix elsewhere; here only the whole id counts.</summary>
    [Fact]
    public void AnIdThatOnlyPrefixesTheOneOnTheLineIsNotAMatch() =>
        Assert.False(ProcessCommandLine.Mentions($"claude --resume {Id}-fork", Id));

    [Fact]
    public void APrefixOfTheIdDoesNotMatchTheWholeOne() =>
        Assert.False(ProcessCommandLine.Mentions($"claude --resume {Id}", "344df963"));

    [Fact]
    public void CaseDoesNotMatter() =>
        Assert.True(ProcessCommandLine.Mentions($"claude --resume {Id.ToUpperInvariant()}", Id));

    [Fact]
    public void AnIdAtTheVeryEndStillMatches() =>
        Assert.True(ProcessCommandLine.Mentions($"claude --resume {Id}", Id));

    /// <summary>A session named after its id would otherwise match itself twice; once is enough.</summary>
    [Fact]
    public void TheIdInsideAQuotedNameStillCounts() =>
        Assert.True(ProcessCommandLine.Mentions($@"claude --resume {Id} --name ""{Id}""", Id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoCommandLineIsNoMatch(string? line) =>
        Assert.False(ProcessCommandLine.Mentions(line, Id));

    [Fact]
    public void NoIdIsNoMatch() =>
        Assert.False(ProcessCommandLine.Mentions("claude --resume something", ""));

    // --- the P/Invoke itself ------------------------------------------------

    /// <summary>
    /// Reading a command line means walking another process's PEB at hard-coded x64 offsets.
    /// Nothing in the type system holds those in place, so this reads the one process whose
    /// command line is already known — ours.
    /// </summary>
    [Fact]
    public void OurOwnCommandLineCanBeRead()
    {
        var line = ProcessCommandLine.Of(Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "testhost",
            line!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A pid that is not running is no answer, not a crash.</summary>
    [Fact]
    public void ADeadProcessReadsAsNull()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        int pid = process.Id;
        process.Dispose();

        Assert.Null(ProcessCommandLine.Of(pid));
    }

    /// <summary>Nothing is running the id we invented, and the scan says so rather than throwing.</summary>
    [Fact]
    public void NothingResumesAnInventedSession() =>
        Assert.Empty(ProcessCommandLine.ResumingPids("00000000-0000-4000-8000-000000000000"));
}
