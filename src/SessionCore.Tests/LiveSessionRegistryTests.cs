using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The registry file is written by the CLI, not by us, and it is the only bridge from a
/// session id to a running process. These lock the fields the restart feature leans on
/// and the shrugs it has to survive — the file is written while we read it.
/// </summary>
public class LiveSessionRegistryTests
{
    /// <summary>A verbatim entry, so a field rename upstream fails here rather than in the UI.</summary>
    private const string RealEntry = """
        {"pid":16500,"sessionId":"b9e83ad3-8742-4f86-b5e3-40e844f24da1",
         "cwd":"C:\\Users\\kk\\Code\\ontario-address-changes","startedAt":1787361907223,
         "procStart":"134318355060801548","version":"2.1.239","peerProtocol":1,
         "peerFeatures":["notify_idle"],"kind":"interactive","entrypoint":"cli",
         "messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-c35651c01dccbb273a6b9630b50be332",
         "name":"ontario-address-changes-6c","nameSource":"derived","nameSince":1787361907223,
         "status":"idle","updatedAt":1787378054166,"statusUpdatedAt":1787378054166,
         "bridgeSessionId":"session_01NpwuF1HVr5CRthp5YS8SWH"}
        """;

    [Fact]
    public void ReadsEverythingARestartNeeds()
    {
        var live = LiveSessionRegistry.Parse(RealEntry);

        Assert.NotNull(live);
        Assert.Equal(16500, live!.Pid);
        Assert.Equal("b9e83ad3-8742-4f86-b5e3-40e844f24da1", live.SessionId);
        Assert.Equal(@"C:\Users\kk\Code\ontario-address-changes", live.Cwd);
        Assert.Equal("2.1.239", live.Version);
        Assert.Equal("idle", live.Status);
        Assert.Equal("interactive", live.Kind);
        Assert.Equal("cli", live.Entrypoint);
        Assert.Equal("derived", live.NameSource);
        Assert.True(live.RemoteControl);
    }

    /// <summary>The desktop app publishes no status and no bridge — both absences are real states.</summary>
    [Fact]
    public void ADesktopEntryHasNeitherStatusNorRemoteControl()
    {
        var live = LiveSessionRegistry.Parse("""
            {"pid":20096,"sessionId":"576aba4f-415c-4a1e-88eb-479b914721a8","cwd":"C:\\Users\\kk\\Code\\cowork",
             "version":"2.1.237","kind":"interactive","entrypoint":"claude-desktop","name":"cowork-a5"}
            """);

        Assert.NotNull(live);
        Assert.Null(live!.Status);
        Assert.Null(live.StatusUpdatedAt);
        Assert.False(live.RemoteControl);
    }

    [Fact]
    public void StatusTimestampBecomesALocalTime()
    {
        var live = LiveSessionRegistry.Parse(RealEntry);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787378054166).LocalDateTime,
            live!.StatusUpdatedAt);
    }

    /// <summary>An entry naming no session or no process maps to nothing we can act on.</summary>
    [Theory]
    [InlineData("""{"pid":123,"kind":"interactive"}""")]
    [InlineData("""{"sessionId":"abc","kind":"interactive"}""")]
    [InlineData("""{"sessionId":"","pid":123}""")]
    public void EntriesWithoutBothHalvesOfTheMappingAreDropped(string json) =>
        Assert.Null(LiveSessionRegistry.Parse(json));

    /// <summary>Fields we do not know about are the CLI's business, not a parse failure.</summary>
    [Fact]
    public void UnknownFieldsAreIgnored() =>
        Assert.NotNull(LiveSessionRegistry.Parse(
            """{"pid":1,"sessionId":"s","kind":"interactive","somethingNew":{"deep":[1,2]}}"""));

    /// <summary>A directory that has never existed is simply nothing running.</summary>
    [Fact]
    public void AMissingRegistryDirectoryIsEmptyNotAnError() =>
        Assert.Empty(LiveSessionRegistry.Read(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid())));

    /// <summary>
    /// The CLI rewrites these files under us, so a half-written one must cost its own entry
    /// and nothing else.
    /// </summary>
    [Fact]
    public void AHalfWrittenFileDoesNotTakeTheGoodOnesDownWithIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "registry-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "1.json"), RealEntry);
            File.WriteAllText(Path.Combine(dir, "2.json"), """{"pid":2,"sessi""");

            var all = LiveSessionRegistry.Read(dir);
            Assert.Single(all);
            Assert.Equal(16500, all[0].Pid);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>
/// Staleness is the trigger for the whole feature, and a false positive sends someone to
/// restart a session for nothing.
/// </summary>
public class ClaudeInstallTests
{
    [Theory]
    [InlineData("2.1.239", "2.1.240", true)]
    [InlineData("2.1.235", "2.1.240", true)]
    [InlineData("2.1.240", "2.1.240", false)]
    [InlineData("2.2.0", "2.1.240", false)]
    public void StaleMeansBehindTheInstalledBuild(string running, string installed, bool expected) =>
        Assert.Equal(expected, ClaudeInstall.IsStale(running, installed));

    /// <summary>Dotted text is not a decimal: 240 is above 99, and above 39.</summary>
    [Fact]
    public void VersionsCompareAsNumbersNotText()
    {
        Assert.True(ClaudeInstall.Compare("2.1.240", "2.1.99") > 0);
        Assert.True(ClaudeInstall.Compare("2.1.240", "2.1.39") > 0);
        Assert.True(ClaudeInstall.Compare("2.10.0", "2.9.0") > 0);
    }

    /// <summary>Not knowing is never reported as out of date.</summary>
    [Theory]
    [InlineData(null, "2.1.240")]
    [InlineData("2.1.239", null)]
    [InlineData("nightly", "2.1.240")]
    [InlineData("2.1.239", "some-tag")]
    public void UnknownVersionsAreNeverCalledStale(string? running, string? installed) =>
        Assert.False(ClaudeInstall.IsStale(running, installed));

    [Fact]
    public void NewestInstalledIsTheHighestVersionOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "versions-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var v in new[] { "2.1.238", "2.1.240", "2.1.239" })
                File.WriteAllText(Path.Combine(dir, v), "");
            File.WriteAllText(Path.Combine(dir, "install.tmp"), "");   // installer scratch

            Assert.Equal("2.1.240", ClaudeInstall.NewestVersion(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Updating renames the binary out from under the processes still running it, so a
    /// session reports <c>claude.exe.old.&lt;timestamp&gt;</c> from the moment it falls
    /// behind. Matching only "claude" drops every session the instant it becomes the one
    /// worth restarting — which is how this was found: no dots, and a sweep of nothing.
    /// </summary>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("CLAUDE", true)]
    [InlineData("claude.exe.old.1787410246719", true)]
    [InlineData("node", false)]
    [InlineData("claudette", false)]
    [InlineData("powershell", false)]
    [InlineData(null, false)]
    public void AClaudeThatOutlivedItsOwnBinaryIsStillAClaude(string? processName, bool expected) =>
        Assert.Equal(expected, ClaudeInstall.IsClaudeProcess(processName));

    [Fact]
    public void NoVersionsDirectoryMeansWeDoNotKnow() =>
        Assert.Null(ClaudeInstall.NewestVersion(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));
}
