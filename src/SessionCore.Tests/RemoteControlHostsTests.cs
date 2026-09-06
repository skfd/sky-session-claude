using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A `claude rc` host is invisible in every place Sky normally looks: it publishes no entry in
/// the session registry, and — started without a pre-created session, as standby starts it —
/// writes no `bridge-pointer.json` either. The process is the only record that it exists, so
/// these pin how one is recognised: a claude image, the `rc` verb, and a working directory.
/// </summary>
public class RemoteControlHostsTests
{
    private static IReadOnlyList<RemoteControlHost> Running(
        IEnumerable<ProcRef> processes,
        Func<int, string?> commandLine,
        Func<int, string?> workingDirectory) =>
        RemoteControlHosts.Running(processes, commandLine, workingDirectory, _ => null);

    [Fact]
    public void AHostIsAClaudeRunningRcInAFolder()
    {
        var hosts = Running(
            [new ProcRef(4242, "claude")],
            _ => @"""C:\Users\kk\.local\bin\claude.exe"" rc --remote-control-session-name-prefix demo --spawn=same-dir",
            _ => @"C:\Users\kk\Code\demo\");

        var host = Assert.Single(hosts);
        Assert.Equal(4242, host.Pid);
        Assert.Equal(@"C:\Users\kk\Code\demo", host.Folder);   // the trailing slash Windows keeps is dropped
        Assert.Equal("demo", host.Project);
        Assert.Contains("rc --remote-control-session-name-prefix", host.CommandLine);
    }

    /// <summary>
    /// A bridged session, a plain session and a resumed one are all claude processes in a
    /// folder, and none of them is a host. The verb is what makes one.
    /// </summary>
    [Theory]
    [InlineData(@"""C:\claude.exe"" --remote-control")]
    [InlineData(@"""C:\claude.exe"" --resume abc123 --remote-control")]
    [InlineData(@"""C:\claude.exe""")]
    [InlineData(@"""C:\claude.exe"" rcx")]
    public void AClaudeThatIsNotRunningRcIsNotAHost(string commandLine) =>
        Assert.Empty(Running([new ProcRef(1, "claude")], _ => commandLine, _ => @"C:\Code\demo\"));

    /// <summary>Only claude images are even asked; a shell in the folder is not a host.</summary>
    [Fact]
    public void OnlyClaudeProcessesAreConsidered() =>
        Assert.Empty(Running([new ProcRef(1, "powershell")], _ => "powershell.exe rc", _ => @"C:\Code\demo\"));

    /// <summary>
    /// An update renames the running binary out from under a host, and it keeps serving under
    /// the new name — so it is still found, and reads as stale.
    /// </summary>
    [Fact]
    public void ASupersededHostIsStillAHostAndIsStale()
    {
        var hosts = Running(
            [new ProcRef(7, "claude.exe.old.1787697313311")],
            _ => @"""C:\claude.exe"" rc",
            _ => @"C:\Code\demo\");

        Assert.True(Assert.Single(hosts).Stale);
    }

    /// <summary>
    /// A process that will not say where it is working is not a host — it may have exited
    /// mid-inspection, or belong to someone else. Nothing is opened on the strength of a no,
    /// and nothing is skipped on the strength of a guess.
    /// </summary>
    [Fact]
    public void AProcessWithNoReadableFolderIsNotAHost()
    {
        Assert.Empty(Running([new ProcRef(1, "claude")], _ => @"""C:\claude.exe"" rc", _ => null));
        Assert.Empty(Running([new ProcRef(1, "claude")], _ => null, _ => @"C:\Code\demo\"));
    }

    [Fact]
    public void FindsTheHostServingAFolderWhateverTheSpelling()
    {
        var hosts = Running(
            [new ProcRef(1, "claude"), new ProcRef(2, "claude")],
            _ => @"""C:\claude.exe"" rc",
            pid => pid == 1 ? @"C:\Users\kk\Code\demo\" : @"C:\Users\kk\Code\other\");

        Assert.Equal(1, RemoteControlHosts.ServingFolder(hosts, @"C:\Users\kk\Code\demo")!.Pid);
        Assert.Equal(1, RemoteControlHosts.ServingFolder(hosts, @"c:/users/kk/code/DEMO/")!.Pid);
        Assert.Null(RemoteControlHosts.ServingFolder(hosts, @"C:\Users\kk\Code\demo-two"));
    }

    [Theory]
    [InlineData("rc", true)]
    [InlineData("rc --spawn=same-dir", true)]
    [InlineData("RC", true)]
    [InlineData("rcx", false)]
    [InlineData("--rc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheVerbHasToBeTheWholeFirstToken(string? arguments, bool expected) =>
        Assert.Equal(expected, RemoteControlHosts.IsHostCommand(arguments));
}
