namespace SessionCore;

/// <summary>What one standby run did, by tab.</summary>
public sealed record StandbyOutcome
{
    /// <summary>Hosts typed back into the tab their last host died in.</summary>
    public required IReadOnlyList<StandbyTarget> Revived { get; init; }

    /// <summary>Hosts given a new tab — including any whose old tab would not take the line.</summary>
    public required IReadOnlyList<StandbyTarget> Opened { get; init; }

    /// <summary>Dead tabs closed.</summary>
    public required int Closed { get; init; }

    /// <summary>Dead tabs that would not take the <c>exit</c>, and are still there.</summary>
    public required int CouldNotClose { get; init; }

    /// <summary>"7 revived in their old tabs, 1 in a new tab, 2 dead tabs closed".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Revived.Count > 0) parts.Add($"{Revived.Count} back up in {(Revived.Count == 1 ? "its old tab" : "their old tabs")}");
        if (Opened.Count > 0) parts.Add($"{Opened.Count} in {(Opened.Count == 1 ? "a new tab" : "new tabs")}");
        if (Closed > 0) parts.Add($"{Closed} dead tab{(Closed == 1 ? "" : "s")} closed");
        if (CouldNotClose > 0) parts.Add($"{CouldNotClose} dead tab{(CouldNotClose == 1 ? "" : "s")} would not close");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Carries out a standby plan: the window, the CLI and the keeper all open hosts this way, so
/// a dead tab is reused or closed whichever of them gets there first.
///
/// Trust is the caller's to have granted already — it is a question with an answer, and only
/// the caller knows whether anyone gave one.
/// </summary>
public static class StandbyLauncher
{
    public static StandbyOutcome Launch(StandbyPlan plan)
    {
        var revived = new List<StandbyTarget>();
        var fresh = new List<StandbyTarget>();

        foreach (var target in plan.Open)
        {
            // The same line HostRestarter types, into the same kind of shell — the folder
            // first, then the host it was opened to run. A shell that will not take it has
            // gone since the plan was made, and the target gets a tab of its own instead.
            if (target.ReviveIn is { } shell
                && ConsoleInput.SendLine(shell.Pid, LaunchLine.HostAgain(target.Folder, shell.HostCommand)))
                revived.Add(target);
            else
                fresh.Add(target);
        }

        // The project names the tab and is the prefix of every session the host creates —
        // see ClaudeLaunch.Host for why leaving it off would name them all after this machine.
        TerminalLauncher.StartTabs(
            fresh.Select(t => new TerminalTab(t.Folder, t.Project, ClaudeLaunch.Host(t.Project))).ToList(),
            TerminalLauncher.StandbyWindow);

        // `exit 0` rather than a bare exit: Windows Terminal closes a tab on its own only when
        // the shell leaves cleanly, and a bare exit in a shell whose last command failed — the
        // host that died — can hand that failure back as the exit code and leave the tab up.
        var closed = plan.Close.Count(shell => ConsoleInput.SendLine(shell.Pid, "exit 0"));

        return new StandbyOutcome
        {
            Revived = revived,
            Opened = fresh,
            Closed = closed,
            CouldNotClose = plan.Close.Count - closed,
        };
    }
}
