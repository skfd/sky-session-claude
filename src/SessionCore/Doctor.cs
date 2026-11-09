using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionCore;

/// <summary>One thing the doctor looked at, and what it found.</summary>
public sealed record DoctorFinding
{
    /// <summary>sky, keeper, stores, hosts or home.</summary>
    public required string Check { get; init; }

    /// <summary>ok, warn or fail. Only a fail makes the verdict a failure.</summary>
    public required string Level { get; init; }

    public required string Message { get; init; }
}

public sealed record DoctorReport
{
    public required DateTimeOffset At { get; init; }
    public required bool Ok { get; init; }
    public required IReadOnlyList<DoctorFinding> Findings { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<DoctorFinding> Failures => Findings.Where(f => f.Level == Doctor.Fail);

    public const string FileName = "doctor.json";

    public static string DefaultPath => Path.Combine(DispositionStore.DefaultDir(), FileName);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Write beside and move over. Never throws.</summary>
    public void Write(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", ToJson());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch { /* the verdict is on stdout too */ }
    }

    public static DoctorReport? Read(string? path = null)
    {
        try { return JsonSerializer.Deserialize<DoctorReport>(File.ReadAllText(path ?? DefaultPath), Json); }
        catch { return null; }
    }
}

/// <summary>Everything the doctor reads, gathered by the caller so the rules can be tested.</summary>
public sealed record DoctorInputs
{
    public required DateTimeOffset Now { get; init; }
    public Heartbeat? Heartbeat { get; init; }
    public required Func<int, bool> PidAlive { get; init; }

    /// <summary>A shared store's file's write time (UTC), by file name.</summary>
    public required Func<string, DateTime> StampOf { get; init; }

    /// <summary>This week's standby plan, as <c>standby --dry-run</c> would make it.</summary>
    public required StandbyPlan Plan { get; init; }

    /// <summary>The tail of <c>standby.log</c>.</summary>
    public required IReadOnlyList<string> LogLines { get; init; }

    /// <summary>How many <c>.claude.json.bak-*</c> sit in the home folder.</summary>
    public required int BackupCount { get; init; }
}

/// <summary>
/// Watches the watchers, on the live machine: the checks no test before a release can make,
/// because what they catch only happens days into a run.
///
/// Hosts dying after a sweep is <c>claude rc</c>'s own doing, and the keeper inside Sky puts
/// them back. What nothing watched was the keeper — or the reader beside it, which went six
/// weeks holding whatever declarations it had at launch. So this reads Sky's
/// <see cref="Heartbeat"/>, the standby plan and <c>standby.log</c>, and says, for each:
///
/// <list type="bullet">
/// <item><b>sky</b> — running, and its tick turning.</item>
/// <item><b>keeper</b> — on, and taking its turn every minute.</item>
/// <item><b>stores</b> — every shared store Sky holds is the write on disk, give or take a
/// tick. A bare heartbeat proves Sky alive; this proves it still reading.</item>
/// <item><b>hosts</b> — each of this week's projects has a host, or the keeper is visibly on
/// it. A host missing while the keeper backs off (up to an hour) is the keeper doing its job,
/// a warning; one the keeper never looked at, or one it will never fix because the folder was
/// never trusted, is a failure. Crying wolf every morning would cost the verdict its reader,
/// so the line is drawn at what needs a person.</item>
/// <item><b>home</b> — the trust grant's backups stay pruned (10-28).</item>
/// </list>
/// </summary>
public static class Doctor
{
    public const string OkLevel = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";

    /// <summary>Sky beats every half minute; two missed is a tick that stopped.</summary>
    public static readonly TimeSpan BeatStale = TimeSpan.FromMinutes(2);

    /// <summary>The keeper looks every minute.</summary>
    public static readonly TimeSpan KeeperStale = TimeSpan.FromMinutes(3);

    /// <summary>A keeper on a missing host writes about it every minute it looks.</summary>
    public static readonly TimeSpan LogRecent = TimeSpan.FromMinutes(5);

    /// <summary>A write this close before the beat may simply not have been ticked yet.</summary>
    public static readonly TimeSpan StoreSlack = TimeSpan.FromSeconds(10);

    public const int MaxBackups = ClaudeTrust.BackupsKept;

    public static DoctorReport Examine(DoctorInputs inputs)
    {
        var findings = new List<DoctorFinding>();
        var beat = inputs.Heartbeat;
        var skyUp = CheckSky(inputs, findings);

        if (skyUp)
        {
            CheckKeeper(inputs, beat!, findings);
            CheckStores(inputs, beat!, findings);
        }

        CheckHosts(inputs, skyUp, findings);
        CheckHome(inputs, findings);

        return new DoctorReport
        {
            At = inputs.Now,
            Ok = findings.All(f => f.Level != Fail),
            Findings = findings,
        };
    }

    private static bool CheckSky(DoctorInputs inputs, List<DoctorFinding> findings)
    {
        var beat = inputs.Heartbeat;
        if (beat is null)
        {
            findings.Add(F("sky", Fail,
                "Sky has written no heartbeat: it is not running, or it is older than this check."));
            return false;
        }
        if (!inputs.PidAlive(beat.Pid))
        {
            findings.Add(F("sky", Fail,
                $"Sky is not running: pid {beat.Pid} last beat at {Clock(beat.At)}, and nothing keeps standby up."));
            return false;
        }
        if (inputs.Now - beat.At > BeatStale)
        {
            findings.Add(F("sky", Fail,
                $"Sky is running (pid {beat.Pid}) but its tick stopped at {Clock(beat.At)}."));
            return false;
        }

        findings.Add(F("sky", OkLevel, $"Sky {beat.Version} is running (pid {beat.Pid})."));
        return true;
    }

    private static void CheckKeeper(DoctorInputs inputs, Heartbeat beat, List<DoctorFinding> findings)
    {
        if (!beat.KeeperOn)
            findings.Add(F("keeper", Warn, "This Sky runs no keeper (a --multi instance), so nothing puts hosts back."));
        else if (beat.KeeperLookedAt is not { } looked)
            findings.Add(F("keeper", Fail, "The keeper has not taken a single turn since Sky started."));
        else if (inputs.Now - looked > KeeperStale)
            findings.Add(F("keeper", Fail, $"The keeper last looked at {Clock(looked)} and has stopped."));
        else
            findings.Add(F("keeper", OkLevel, $"The keeper looked at {Clock(looked)}."));
    }

    private static void CheckStores(DoctorInputs inputs, Heartbeat beat, List<DoctorFinding> findings)
    {
        var behind = new List<string>();
        foreach (var (file, held) in beat.Stores.OrderBy(s => s.Key))
        {
            var onDisk = inputs.StampOf(file);
            // Written after the last beat, or just before it: not yet a tick to have read it in.
            if (onDisk == held || onDisk > beat.At.UtcDateTime - StoreSlack) continue;
            behind.Add($"{file} (holds {Clock(held)}, file written {Clock(onDisk)})");
        }

        findings.Add(behind.Count == 0
            ? F("stores", OkLevel, $"Sky holds the latest write of all {beat.Stores.Count} shared stores.")
            : F("stores", Fail, "Sky has stopped re-reading: " + string.Join("; ", behind) + "."));
    }

    private static void CheckHosts(DoctorInputs inputs, bool skyUp, List<DoctorFinding> findings)
    {
        var served = inputs.Plan.Skipped.Count(s => s.Reason.StartsWith("already on standby"));
        var beat = inputs.Heartbeat;
        var mayGrant = skyUp && beat!.KeeperMayGrantTrust;
        var keeperOn = skyUp && beat!.KeeperOn;
        var recent = RecentKeeperLines(inputs);

        foreach (var missing in inputs.Plan.Open)
        {
            var name = missing.Project;
            if (missing.NeedsTrust && !mayGrant)
                findings.Add(F("hosts", Fail,
                    $"{name} has no host and the keeper will never give it one: Claude Code was never trusted "
                    + "with the folder. Press Standby, or run `SessionCli standby --in` there."));
            else if (!keeperOn)
                findings.Add(F("hosts", Fail, $"{name} has no host, and no keeper is running to put one back."));
            else if (recent.LastOrDefault(l => Names(l, name)) is { } said)
                findings.Add(F("hosts", Warn, $"{name} has no host; the keeper is on it: {said}"));
            else
                findings.Add(F("hosts", Fail,
                    $"{name} has no host and the keeper has not written about it in {LogRecent.TotalMinutes:0} min."));
        }

        if (inputs.Plan.Open.Count == 0)
            findings.Add(F("hosts", OkLevel, $"All {served} of this week's projects have a host."));
    }

    private static void CheckHome(DoctorInputs inputs, List<DoctorFinding> findings) =>
        findings.Add(inputs.BackupCount > MaxBackups
            ? F("home", Fail,
                $"{inputs.BackupCount} .claude.json.bak-* copies in the home folder; trust grants should keep {MaxBackups}.")
            : F("home", OkLevel, $"{inputs.BackupCount} .claude.json backup(s) in the home folder."));

    /// <summary>The keeper's lines from the last few minutes, without their time stamps.</summary>
    private static List<string> RecentKeeperLines(DoctorInputs inputs)
    {
        var since = inputs.Now.LocalDateTime - LogRecent;
        var lines = new List<string>();
        foreach (var line in inputs.LogLines)
        {
            if (line.Length < 21) continue;
            if (!DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at) || at < since) continue;
            var text = line[19..].Trim();
            if (text.StartsWith("keeper:")) lines.Add(text);
        }
        return lines;
    }

    /// <summary>Whether a log line names the project as a whole word — hamilton is not hamilton-x.</summary>
    private static bool Names(string line, string project) =>
        Regex.IsMatch(line, $@"(?<![\w-]){Regex.Escape(project)}(?![\w-])", RegexOptions.IgnoreCase);

    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Clock(DateTime utc) =>
        utc == DateTime.MinValue ? "never" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static DoctorFinding F(string check, string level, string message) =>
        new() { Check = check, Level = level, Message = message };
}
