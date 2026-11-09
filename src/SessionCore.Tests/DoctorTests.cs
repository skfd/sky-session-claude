using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The doctor's rules, one per regression it exists for, and the line it draws between what
/// needs a person and what the keeper is already doing.
/// </summary>
public class DoctorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTime Written = Now.UtcDateTime.AddMinutes(-30);

    private static Heartbeat Beat(Func<Heartbeat, Heartbeat>? tweak = null)
    {
        var beat = new Heartbeat
        {
            At = Now.AddSeconds(-20),
            Pid = 42,
            Version = "1.15.3-dev",
            KeeperOn = true,
            KeeperLookedAt = Now.AddSeconds(-40),
            Stores = new() { ["declarations.json"] = Written, ["names.json"] = Written },
        };
        return tweak is null ? beat : tweak(beat);
    }

    private static StandbyTarget Missing(string project, bool needsTrust = false) => new()
    {
        Folder = $@"C:\Code\{project}", Project = project, LastActive = Now.LocalDateTime, NeedsTrust = needsTrust,
    };

    private static StandbySkip Served(string project) => new()
    {
        Folder = $@"C:\Code\{project}", Project = project,
        Reason = "already on standby — a claude rc host (pid 7) is serving this folder",
    };

    private static string Log(int minutesAgo, string text) =>
        $"{Now.LocalDateTime.AddMinutes(-minutesAgo):yyyy-MM-dd HH:mm:ss}  {text}";

    private static DoctorReport Run(
        Heartbeat? beat = null,
        bool noBeat = false,
        bool alive = true,
        Func<string, DateTime>? stampOf = null,
        IReadOnlyList<StandbyTarget>? missing = null,
        IReadOnlyList<string>? log = null,
        int backups = 2) =>
        Doctor.Examine(new DoctorInputs
        {
            Now = Now,
            Heartbeat = noBeat ? null : beat ?? Beat(),
            PidAlive = _ => alive,
            StampOf = stampOf ?? (_ => Written),
            Plan = new StandbyPlan { Open = missing ?? [], Skipped = [Served("a"), Served("b")] },
            LogLines = log ?? [],
            BackupCount = backups,
        });

    private static DoctorFinding Only(DoctorReport report, string check) =>
        Assert.Single(report.Findings, f => f.Check == check);

    [Fact]
    public void AHealthyMachineIsOk()
    {
        var report = Run();
        Assert.True(report.Ok);
        Assert.All(report.Findings, f => Assert.Equal(Doctor.OkLevel, f.Level));
        Assert.Contains("All 2", Only(report, "hosts").Message);
    }

    [Fact]
    public void NoHeartbeatFails() =>
        Assert.Equal(Doctor.Fail, Only(Run(noBeat: true), "sky").Level);

    [Fact]
    public void AHeartbeatFromADeadSkyFails() =>
        Assert.Equal(Doctor.Fail, Only(Run(alive: false), "sky").Level);

    [Fact]
    public void ALiveSkyWhoseTickStoppedFails()
    {
        var report = Run(Beat(b => b with { At = Now.AddMinutes(-10) }));
        Assert.Contains("tick stopped", Only(report, "sky").Message);
        Assert.DoesNotContain(report.Findings, f => f.Check is "keeper" or "stores");
    }

    /// <summary>The 10-12 regression, seen from outside: Sky ticking, holding an old write.</summary>
    [Fact]
    public void ASkyThatStoppedReReadingFails()
    {
        var report = Run(stampOf: f => f == "declarations.json" ? Now.UtcDateTime.AddMinutes(-5) : Written);
        var stores = Only(report, "stores");
        Assert.Equal(Doctor.Fail, stores.Level);
        Assert.Contains("declarations.json", stores.Message);
        Assert.DoesNotContain("names.json", stores.Message);
    }

    [Fact]
    public void AWriteSinceTheLastBeatIsNotYetBehind() =>
        Assert.Equal(Doctor.OkLevel,
            Only(Run(stampOf: _ => Now.UtcDateTime.AddSeconds(-15)), "stores").Level);

    [Fact]
    public void AKeeperThatStoppedLookingFails() =>
        Assert.Equal(Doctor.Fail,
            Only(Run(Beat(b => b with { KeeperLookedAt = Now.AddMinutes(-20) })), "keeper").Level);

    [Fact]
    public void NoKeeperAtAllIsAWarningAndItsMissingHostsFail()
    {
        var report = Run(Beat(b => b with { KeeperOn = false }), missing: [Missing("x")]);
        Assert.Equal(Doctor.Warn, Only(report, "keeper").Level);
        Assert.Equal(Doctor.Fail, Only(report, "hosts").Level);
    }

    /// <summary>Backing off is the keeper doing its job; a fail here would cry wolf daily.</summary>
    [Fact]
    public void AMissingHostTheKeeperIsOnIsOnlyAWarning()
    {
        var report = Run(missing: [Missing("hamilton")],
            log: [Log(1, "keeper: hamilton held — its last host went down — trying again in 16 min")]);
        Assert.True(report.Ok);
        Assert.Equal(Doctor.Warn, Only(report, "hosts").Level);
    }

    [Fact]
    public void AMissingHostTheKeeperNeverMentionedFails()
    {
        var report = Run(missing: [Missing("hamilton")],
            log: [Log(30, "keeper: hamilton held — its last host went down — trying again in 2 min"),
                  Log(1, "keeper: hamilton-address-import held — something")]);
        Assert.False(report.Ok);
        Assert.Contains("has not written about it", Only(report, "hosts").Message);
    }

    /// <summary>The state the live log was in on 2026-11-09: held every minute, forever.</summary>
    [Fact]
    public void AnUntrustedFolderTheKeeperMayNotGrantFailsEvenWhileItIsMentioned()
    {
        var report = Run(missing: [Missing("thunder-bay", needsTrust: true)],
            log: [Log(1, "keeper: thunder-bay held — never trusted with Claude Code — the Standby button records the trust and opens it")]);
        Assert.Contains("never trusted", Only(report, "hosts").Message);
        Assert.False(report.Ok);
    }

    [Fact]
    public void AnUntrustedFolderIsTheKeepersWhenItMayGrant()
    {
        var report = Run(Beat(b => b with { KeeperMayGrantTrust = true }),
            missing: [Missing("thunder-bay", needsTrust: true)],
            log: [Log(1, "keeper: putting back thunder-bay; 0 dead tab(s) to close")]);
        Assert.Equal(Doctor.Warn, Only(report, "hosts").Level);
    }

    /// <summary>The 10-28 regression.</summary>
    [Fact]
    public void BackupsPilingUpFail() =>
        Assert.Equal(Doctor.Fail, Only(Run(backups: 24), "home").Level);

    [Fact]
    public void TheVerdictRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"doctor-{Guid.NewGuid():N}.json");
        try
        {
            var report = Run(missing: [Missing("x")]);
            report.Write(path);
            var read = DoctorReport.Read(path);
            Assert.NotNull(read);
            Assert.False(read!.Ok);
            Assert.Equal(report.Findings.Count, read.Findings.Count);
            Assert.Single(read.Failures);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheHeartbeatRoundTripsWithUtcStamps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beat-{Guid.NewGuid():N}.json");
        try
        {
            Beat().Write(path);
            var read = Heartbeat.Read(path)!;
            Assert.Equal(Written, read.Stores["declarations.json"]);
            Assert.Equal(DateTimeKind.Utc, read.Stores["declarations.json"].Kind);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }
}
