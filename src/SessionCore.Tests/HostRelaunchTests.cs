using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The parts of a host restart that can be decided without a process: how a host is told to
/// be stale, what goes back into the terminal, and how many conversations it is serving.
/// </summary>
public class HostRelaunchTests
{
    // --- staleness ----------------------------------------------------------

    /// <summary>
    /// The rename an update leaves behind is the only staleness signal a host has, since it
    /// publishes no version to compare.
    /// </summary>
    [Theory]
    [InlineData("claude.exe.old.1787697313311", true)]
    [InlineData("CLAUDE.EXE.OLD.1787697313311", true)]
    [InlineData("claude", false)]
    [InlineData("claude.exe", false)]
    [InlineData(null, false)]
    public void SupersededIsTheRenamedImage(string? processName, bool expected) =>
        Assert.Equal(expected, ClaudeInstall.IsSuperseded(processName));

    /// <summary>A superseded process is still a claude, which is how it stays visible at all.</summary>
    [Fact]
    public void ASupersededImageIsStillAClaudeProcess() =>
        Assert.True(ClaudeInstall.IsClaudeProcess("claude.exe.old.1787697313311"));

    // --- reading the command line -------------------------------------------

    [Theory]
    [InlineData(@"""C:\Users\kk\.local\bin\claude.exe"" rc", "rc")]
    [InlineData(@"""C:\Program Files\claude\claude.exe"" rc --spawn=same-dir", "rc --spawn=same-dir")]
    [InlineData(@"C:\bin\claude.exe rc --create-session-in-dir", "rc --create-session-in-dir")]
    [InlineData(@"""C:\bin\claude.exe""", "")]
    [InlineData(@"C:\bin\claude.exe", "")]
    public void ArgumentsAreWhateverFollowsTheExecutable(string commandLine, string expected) =>
        Assert.Equal(expected, ProcessCommandLine.ArgumentsOf(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"""C:\bin\claude.exe rc")]     // an opening quote that never closes
    public void AnUnreadableCommandLineHasNoArguments(string? commandLine) =>
        Assert.Null(ProcessCommandLine.ArgumentsOf(commandLine));

    // --- the line that puts it back -----------------------------------------

    /// <summary>
    /// A host started by hand comes back as the host it was. Bolting standby's flags onto it
    /// would hand back a different server from the one the restart took away.
    /// </summary>
    [Fact]
    public void ABareHostComesBackBare() =>
        Assert.Equal(
            @"cd 'C:\Users\kk\Code\demo'; claude rc",
            LaunchLine.HostAgain(@"C:\Users\kk\Code\demo", @"""C:\Users\kk\.local\bin\claude.exe"" rc"));

    [Fact]
    public void StandbysFlagsAreCarriedOverVerbatim() =>
        Assert.Equal(
            @"cd 'C:\Users\kk\Code\demo'; claude rc --remote-control-session-name-prefix demo --spawn=same-dir",
            LaunchLine.HostAgain(@"C:\Users\kk\Code\demo",
                @"""C:\claude.exe"" rc --remote-control-session-name-prefix demo --spawn=same-dir"));

    /// <summary>
    /// With no command line to read, standby's line is the honest guess — and it is what the
    /// overwhelming majority of hosts were started with anyway.
    /// </summary>
    [Fact]
    public void AnUnreadableCommandLineFallsBackToWhatStandbyWouldOpen() =>
        Assert.Equal(
            LaunchLine.HostIn(@"C:\Users\kk\Code\demo", "demo"),
            LaunchLine.HostAgain(@"C:\Users\kk\Code\demo", null));

    /// <summary>
    /// Anything that is not the <c>rc</c> verb is not understood, and guessing at it would be
    /// worse than saying so: <c>rcx</c> is not this command.
    /// </summary>
    [Theory]
    [InlineData(@"""C:\claude.exe"" rcx --whatever")]
    [InlineData(@"""C:\claude.exe"" --resume abc123")]
    [InlineData(@"""C:\claude.exe""")]
    public void SomethingOtherThanRcIsNotCarriedOver(string commandLine) =>
        Assert.Equal(
            LaunchLine.HostIn(@"C:\Users\kk\Code\demo", "demo"),
            LaunchLine.HostAgain(@"C:\Users\kk\Code\demo", commandLine));

    [Fact]
    public void TheFolderIsQuotedForTheShell() =>
        Assert.Contains(@"cd 'C:\Users\kk\Code\it''s a repo'",
            LaunchLine.HostAgain(@"C:\Users\kk\Code\it's a repo", @"""C:\claude.exe"" rc"));

    // --- counting what a host is serving ------------------------------------

    /// <summary>
    /// Tree snapshots name the file on disk, so a superseded host's children are still
    /// <c>claude.exe</c> — and a child that has been renamed itself counts too.
    /// </summary>
    [Fact]
    public void ConversationsUnderCountsOnlyClaudeChildren()
    {
        var children = new Dictionary<int, List<ProcRef>>
        {
            [4242] =
            [
                new ProcRef(1, "claude.exe"),
                new ProcRef(2, "claude.exe.old.1787697313311"),
                new ProcRef(3, "conhost.exe"),
                new ProcRef(4, "node.exe"),
            ],
        };

        Assert.Equal(2, RemoteControlHosts.ConversationsUnder(4242, children));
    }

    [Fact]
    public void AHostWithNoChildrenServesNothing() =>
        Assert.Equal(0, RemoteControlHosts.ConversationsUnder(4242, new Dictionary<int, List<ProcRef>>()));

    // --- which conversations are a host's -----------------------------------

    private static RemoteControlHost At(string folder, int pid = 4242) => new()
    {
        Pointer = new BridgePointer { SessionId = "session_abc123", Pid = pid },
        Folder = folder,
        ProjectDir = @"C:\Users\kk\.claude\projects\slug",
        ProcessName = "claude.exe.old.1787697313311",
    };

    private static LiveSession Spawned(int pid, string entrypoint = "sdk-cli", string? cwd = null) => new()
    {
        Pid = pid,
        SessionId = $"session-{pid}",
        Cwd = cwd,
        Status = "idle",
        Kind = "interactive",
        Entrypoint = entrypoint,
    };

    [Fact]
    public void AChildInTheProcessTreeIsServed()
    {
        var parents = new Dictionary<int, int> { [5150] = 4242 };
        var served = RemoteControlHosts.Serving(At(@"C:\Code\demo"), [Spawned(5150)], parents);

        Assert.Equal(5150, Assert.Single(served).Pid);
    }

    /// <summary>
    /// The belt to the tree's braces: a conversation spawned through anything in between is
    /// still the host's, and reading it as "serving nothing" is how a busy host gets swept.
    /// </summary>
    [Fact]
    public void AnSdkConversationInTheFolderIsServedEvenWithoutAParentMatch()
    {
        var served = RemoteControlHosts.Serving(
            At(@"C:\Code\demo"), [Spawned(5150, cwd: @"C:\Code\demo\")], new Dictionary<int, int>());

        Assert.Single(served);
    }

    /// <summary>A bridged terminal in the same folder is a session, not a host's conversation.</summary>
    [Fact]
    public void ABridgedTerminalInTheSameFolderIsNotServed()
    {
        var served = RemoteControlHosts.Serving(
            At(@"C:\Code\demo"), [Spawned(5150, entrypoint: "cli", cwd: @"C:\Code\demo")],
            new Dictionary<int, int>());

        Assert.Empty(served);
    }

    [Fact]
    public void AConversationInAnotherFolderIsNotServed()
    {
        var served = RemoteControlHosts.Serving(
            At(@"C:\Code\demo"), [Spawned(5150, cwd: @"C:\Code\other")], new Dictionary<int, int>());

        Assert.Empty(served);
    }

    /// <summary>Both routes finding the same conversation must not count it twice.</summary>
    [Fact]
    public void AConversationFoundBothWaysIsCountedOnce()
    {
        var parents = new Dictionary<int, int> { [5150] = 4242 };
        var served = RemoteControlHosts.Serving(
            At(@"C:\Code\demo"), [Spawned(5150, cwd: @"C:\Code\demo")], parents);

        Assert.Single(served);
    }

    // --- naming one host ----------------------------------------------------

    private static readonly RemoteControlHost[] Hosts =
    [
        At(@"C:\Code\xrm-ribbon"),
        At(@"C:\Code\xrm-document-x-ray"),
        At(@"C:\Code\canimap"),
    ];

    [Fact]
    public void AFolderNamesItsHost() =>
        Assert.Equal("canimap", Assert.Single(RemoteControlHosts.Matching(Hosts, @"C:\Code\canimap\")).Project);

    /// <summary>An exact project name wins before it is treated as a substring of others.</summary>
    [Fact]
    public void AnExactProjectNameIsNotShadowedBySubstrings() =>
        Assert.Equal("xrm-ribbon", Assert.Single(RemoteControlHosts.Matching(Hosts, "xrm-ribbon")).Project);

    [Fact]
    public void AnAmbiguousSubstringComesBackAmbiguous() =>
        Assert.Equal(2, RemoteControlHosts.Matching(Hosts, "xrm").Count);

    [Fact]
    public void NothingMatchingIsEmpty() =>
        Assert.Empty(RemoteControlHosts.Matching(Hosts, "nope"));
}
