namespace SessionCore;

/// <summary>
/// Which of the keeper's "held" reasons are worth a line in <c>standby.log</c> this look.
///
/// The keeper looks every minute, and it used to write every reason every time: a folder with
/// no <c>.git</c>, a project never trusted, each one a line a minute, forever. Three of those
/// filled the log to its trim within a day and pushed out the month of history it exists to
/// keep ("the phone had nothing this morning" needs last night's lines). So a reason is
/// written when it is new or has changed, and not again while it stands.
///
/// A host the keeper is backing off on still gets a line every look, without a rule for it:
/// its reason counts down ("trying again in 16 min", then 15), so it changes every minute.
/// That is the line <see cref="Doctor"/> reads to tell a keeper on the job from one that never
/// looked, and it must keep coming.
/// </summary>
public sealed class KeeperNotes
{
    private Dictionary<string, string> _said = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lines to write for this look's held projects. A project no longer held is
    /// forgotten, so when it is held again its reason is written again.
    /// </summary>
    public IReadOnlyList<string> Lines(IEnumerable<StandbySkip> held)
    {
        var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var skip in held)
        {
            if (now.ContainsKey(skip.Folder)) continue;
            now[skip.Folder] = skip.Reason;
            if (!_said.TryGetValue(skip.Folder, out var before) || before != skip.Reason)
                lines.Add($"keeper: {skip.Project} held — {skip.Reason}");
        }
        _said = now;
        return lines;
    }
}
