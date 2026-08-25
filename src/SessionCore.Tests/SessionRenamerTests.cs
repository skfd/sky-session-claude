using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The parts of the rename path that can be tested without a live session to speak to: the
/// two facts read out of the registry, and the refusals that must happen before anything is
/// sent. The protocol itself was verified end to end against a running session; what is worth
/// pinning here is that a session with nothing to connect to is refused rather than reported
/// as renamed.
/// </summary>
public class SessionRenamerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sessions-{Guid.NewGuid():N}");

    public SessionRenamerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // --- what the registry says -----------------------------------------------

    [Fact]
    public void ThePipeIsReadFromTheRegistry()
    {
        var live = LiveSessionRegistry.Parse(
            """
            {
              "pid": 10304,
              "sessionId": "257329eb-d9ba-45bb-9844-5b4061bce7d5",
              "messagingSocketPath": "\\\\.\\pipe\\cc-msg-6abe4d3fdbdee3b5d76d99f1c7a83cc1"
            }
            """);

        Assert.Equal(@"\\.\pipe\cc-msg-6abe4d3fdbdee3b5d76d99f1c7a83cc1", live!.MessagingSocketPath);
        Assert.True(live.CanRename);
    }

    /// <summary>A build that publishes no pipe is one we cannot rename in place, and saying so
    /// is better than trying and reporting a silence as success.</summary>
    [Fact]
    public void ASessionWithNoPipeCannotBeRenamed()
    {
        var live = LiveSessionRegistry.Parse("""{ "pid": 1, "sessionId": "abc" }""");

        Assert.Null(live!.MessagingSocketPath);
        Assert.False(live.CanRename);
    }

    /// <summary>
    /// NamedPipeClientStream wants what follows the prefix, not the path the registry
    /// publishes. Both spellings turn up: plain, and under LOCAL\.
    /// </summary>
    [Theory]
    [InlineData(@"\\.\pipe\cc-msg-6abe4d3f", "cc-msg-6abe4d3f")]
    [InlineData(@"\\.\pipe\LOCAL\cc-msg-6abe4d3f", @"LOCAL\cc-msg-6abe4d3f")]
    [InlineData("//./pipe/cc-msg-6abe4d3f", "cc-msg-6abe4d3f")]
    [InlineData("cc-msg-6abe4d3f", "cc-msg-6abe4d3f")]
    public void ThePipeNameIsWhatFollowsThePrefix(string path, string expected) =>
        Assert.Equal(expected, SessionRenamer.PipeNameOf(path));

    // --- the key file ----------------------------------------------------------

    /// <summary>
    /// Only the pid half of the name is knowable from the registry — the hash is the session's
    /// own — so the file is found by globbing rather than by construction.
    /// </summary>
    [Fact]
    public void TheKeyFileIsFoundByPid()
    {
        var expected = Path.Combine(_dir, "10304.750622206362141f4088d6a0032a7ac04036682ac8.key");
        File.WriteAllText(expected, "token");
        File.WriteAllText(Path.Combine(_dir, "12832.b8fa1254a45a31802ff042499a6ff2d310f0848a.key"), "other");

        Assert.Equal(expected, LiveSessionRegistry.KeyPathFor(10304, _dir));
    }

    [Fact]
    public void NoKeyFileIsNoKey() =>
        Assert.Null(LiveSessionRegistry.KeyPathFor(10304, _dir));

    [Fact]
    public void AMissingRegistryDirectoryIsNoKey() =>
        Assert.Null(LiveSessionRegistry.KeyPathFor(10304, Path.Combine(_dir, "nope")));

    // --- refusing before sending -----------------------------------------------

    [Fact]
    public async Task ARenameWithNoNameIsRefused()
    {
        var result = await SessionRenamer.RenameAsync(WithPipe(), "  ");

        Assert.False(result.Ok);
        Assert.Contains("no name", result.Message);
    }

    [Fact]
    public async Task ASessionWithNoPipeIsRefused()
    {
        var result = await SessionRenamer.RenameAsync(new LiveSession { Pid = 1, SessionId = "abc" }, "night shift");

        Assert.False(result.Ok);
        Assert.Contains("no pipe", result.Message);
    }

    /// <summary>
    /// No token means the connection would authenticate with nothing, and an unauthenticated
    /// connection is dropped without a word — so this has to be caught here, where it can
    /// still be reported.
    /// </summary>
    [Fact]
    public async Task ASessionWithNoPeerTokenIsRefused()
    {
        var result = await SessionRenamer.RenameAsync(WithPipe(), "night shift");

        Assert.False(result.Ok);
        Assert.Contains("peer-token", result.Message);
    }

    // A pid that no session on this machine could plausibly be using, so the glob for its key
    // file finds nothing whatever the real registry holds.
    private static LiveSession WithPipe() => new()
    {
        Pid = 999_999_999,
        SessionId = "257329eb-d9ba-45bb-9844-5b4061bce7d5",
        Name = "code-20",
        MessagingSocketPath = @"\\.\pipe\cc-msg-nothing-is-listening-here",
    };
}
