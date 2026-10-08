namespace SessionCore;

/// <summary>
/// Keeps standby up: what Sky does, every minute it is running, so the phone is never left
/// with no projects because a host went away while nobody was at the desk.
///
/// Standby used to be something you did — the button, or the sweep at sign-in — and a host
/// that died afterwards stayed dead until the next one. On a machine that is not signed out
/// for days at a time, that was every host: all nine from one sign-in had exited by the
/// fifth day, and the phone showed nothing until the button was pressed again by hand.
///
/// What this adds to the plan is restraint. A host that cannot come up — the network is
/// down, the account signed out, the folder refuses it — would otherwise be relaunched on
/// every tick, so each folder waits longer after each attempt (two minutes, then four, up to
/// an hour), and a host that stays up for half an hour wipes its folder's slate. And it never
/// answers the folder-trust gate unless it was told it may: that consent belongs to whoever
/// started Sky (<c>--standby</c>) or pressed the button, and the keeper is neither.
///
/// Pure apart from the clock it is handed, like the policies it sits beside.
/// </summary>
public sealed class StandbyKeeper
{
    /// <summary>How long after the first attempt a folder may be tried again.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(2);

    /// <summary>The longest a folder ever waits between attempts.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>A host up this long after its last attempt has come up for good.</summary>
    public static readonly TimeSpan Healthy = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, (DateTime Last, int Attempts)> _tries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The part of <paramref name="plan"/> the keeper may act on now: the folders still waiting
    /// out their last attempt, and the untrusted ones it may not grant, move to the skips. The
    /// dead tabs kept for those folders stay off the close list, so the next attempt reuses
    /// them.
    /// </summary>
    public StandbyPlan Hold(StandbyPlan plan, DateTime now, bool mayGrantTrust)
    {
        // A folder the plan no longer needs is either up or no longer wanted; once that has
        // lasted, its history stops counting against the next time it goes down.
        var wanted = plan.Open.Select(t => Key(t.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _tries.Keys.Where(k => !wanted.Contains(k)).ToList())
            if (now - _tries[folder].Last >= Healthy) _tries.Remove(folder);

        var open = new List<StandbyTarget>();
        var skipped = plan.Skipped.ToList();

        foreach (var target in plan.Open)
        {
            if (target.NeedsTrust && !mayGrantTrust)
                skipped.Add(Skip(target,
                    "never trusted with Claude Code — the Standby button records the trust and opens it"));
            else if (WaitLeft(target.Folder, now) is { } left)
                skipped.Add(Skip(target,
                    $"its last host went down — trying again in {Math.Ceiling(left.TotalMinutes):0} min"));
            else
                open.Add(target);
        }

        return plan with { Open = open, Skipped = skipped };
    }

    /// <summary>Count an attempt at each folder — by the keeper, the button, or a sign-in sweep.</summary>
    public void Attempted(IEnumerable<string> folders, DateTime now)
    {
        foreach (var folder in folders)
        {
            var key = Key(folder);
            var attempts = _tries.TryGetValue(key, out var was) ? was.Attempts + 1 : 1;
            _tries[key] = (now, attempts);
        }
    }

    /// <summary>How long a folder still has to wait, or null when it may be tried now.</summary>
    public TimeSpan? WaitLeft(string folder, DateTime now)
    {
        if (!_tries.TryGetValue(Key(folder), out var tried)) return null;

        var left = tried.Last + WaitAfter(tried.Attempts) - now;
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>Two minutes after the first attempt, doubling, never more than an hour.</summary>
    public static TimeSpan WaitAfter(int attempts)
    {
        var minutes = FirstWait.TotalMinutes * Math.Pow(2, Math.Clamp(attempts, 1, 16) - 1);
        return TimeSpan.FromMinutes(Math.Min(minutes, LongestWait.TotalMinutes));
    }

    private static StandbySkip Skip(StandbyTarget target, string reason) =>
        new() { Folder = target.Folder, Project = target.Project, Reason = reason };

    private static string Key(string folder) => folder.Replace('/', '\\').TrimEnd('\\');
}
