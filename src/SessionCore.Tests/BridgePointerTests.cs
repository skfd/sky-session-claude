using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A `claude rc` host is invisible in every place Sky normally looks: it publishes no entry in
/// the session registry, only the `sdk-cli` conversations it spawns do, and those come and go.
/// `bridge-pointer.json` is the one durable statement that a folder is being served, so these
/// pin the shape of a file written by Claude Code — and the thing that makes reading it safe,
/// which is that it outlives the process it names.
/// </summary>
public class BridgePointerTests
{
    /// <summary>Verbatim, so a field rename upstream fails here rather than as a double launch.</summary>
    private const string RealPointer =
        """
        {"sessionId":"session_01QmjtvSWo8JgkNJmUfEsqDH","environmentId":"env_01QE77TVcmG1N7FEMMLX3V2S",
         "source":"standalone","pid":41840,"procStart":"134315857436907605"}
        """;

    [Fact]
    public void ReadsTheSessionAndThePidServingIt()
    {
        var pointer = BridgePointer.Parse(RealPointer);

        Assert.NotNull(pointer);
        Assert.Equal("session_01QmjtvSWo8JgkNJmUfEsqDH", pointer!.SessionId);
        Assert.Equal(41840, pointer.Pid);
    }

    [Fact]
    public void ShrugsAtSomethingThatIsNotAPointer()
    {
        Assert.Null(BridgePointer.Parse("not json at all"));
        Assert.Null(BridgePointer.Parse("""{"sessionId":"session_01"}"""));      // no pid
        Assert.Null(BridgePointer.Parse("""{"pid":41840}"""));                   // no session
        Assert.Null(BridgePointer.Parse("""{"sessionId":"s","pid":"41840"}"""));  // pid as text
    }

    [Fact]
    public void NoFileMeansNoHost()
    {
        var empty = Directory.CreateTempSubdirectory("sky-bridge-").FullName;
        try
        {
            Assert.Null(BridgePointer.Read(empty));
            Assert.Null(RemoteControlHosts.ServingFrom(empty, _ => true));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// Killing a host leaves the pointer exactly where it was — verified by doing it. So the
    /// file alone proves nothing, and a folder whose host has gone must come back available
    /// rather than skipped forever.
    /// </summary>
    [Fact]
    public void APointerWhoseProcessIsGoneIsNotAHost()
    {
        var dir = Directory.CreateTempSubdirectory("sky-bridge-").FullName;
        try
        {
            File.WriteAllText(BridgePointer.PathIn(dir), RealPointer);

            Assert.NotNull(BridgePointer.Read(dir));                          // the file is there
            Assert.Null(RemoteControlHosts.ServingFrom(dir, _ => false));     // the process is not
            Assert.Equal(41840, RemoteControlHosts.ServingFrom(dir, _ => true)!.Pid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThePointerLivesBesideTheTranscripts()
    {
        Assert.Equal(
            Path.Combine(@"C:\projects\C--Users-kk-Code-cowork", "bridge-pointer.json"),
            BridgePointer.PathIn(@"C:\projects\C--Users-kk-Code-cowork"));
    }
}

/// <summary>
/// The slug is Claude Code's rule, not ours, so it is duplicated in exactly one place for the
/// one caller that has a path and no scanned session to read the folder off.
/// </summary>
public class ProjectDirTests
{
    [Fact]
    public void FlattensAPathTheWayClaudeCodeDoes()
    {
        var scanner = new SessionScanner(@"C:\projects");

        // Every one of the drive colon, the separators and the dot becomes a dash, which is
        // why the drive letter is followed by two. Taken from a real folder on disk.
        Assert.Equal(
            @"C:\projects\C--Users-kk-Code-comentality-com",
            scanner.ProjectDirFor(@"C:\Users\kk\Code\comentality.com"));

        Assert.Equal(
            @"C:\projects\C--Users-kk-Code-sky-session-claude",
            scanner.ProjectDirFor(@"C:\Users\kk\Code\sky-session-claude"));
    }
}
