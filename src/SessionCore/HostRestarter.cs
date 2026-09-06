namespace SessionCore;

/// <summary>
/// Restarts a <c>claude rc</c> host in the terminal it is already sitting in.
///
/// The same three steps as <see cref="SessionRestarter"/> — ask it to quit, wait for it to
/// go, type the relaunch at the shell it hands the terminal back to — because a host is
/// started the way a session is: standby opens a terminal, the shell runs one command, and
/// that command is the host. So there is always a PowerShell underneath to come back to.
///
/// Two things differ, and both are about a host having no registry entry. What goes back in
/// is the host's own command line rather than a resume (see <see cref="LaunchLine.HostAgain"/>),
/// and what confirms it came back is a new <c>claude rc</c> process working in the folder
/// (see <see cref="RemoteControlHosts.Running"/>) — a host writes nothing that says it is
/// serving, so the process is the only place to hear it. That is a weaker proof than a
/// session's registry entry: the process exists the instant the shell runs the line, before
/// it has connected to the account, so "back" here means launched and not yet ready.
/// </summary>
public static class HostRestarter
{
    /// <summary>
    /// How long to wait for a new host to appear in the folder. The shell has to take the
    /// line and the process has to come up far enough to have a working directory; seconds,
    /// usually, but a machine mid-update can be slow to start anything.
    /// </summary>
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Restart <paramref name="host"/> in place. Safety is the caller's to decide
    /// (see <see cref="HostRestartPolicy"/>); this is only the mechanism.
    /// </summary>
    public static async Task<RestartResult> RestartAsync(RemoteControlHost host)
    {
        // Read the shell before anything else: the walk up runs through the host's own
        // process, so once it has gone there is nothing left to walk from.
        if (LiveSessions.ShellFor(host.Pid) is not { } shell)
            return RestartResult.Fail(
                "its terminal has no PowerShell to come back to — restart this host by hand");

        var line = LaunchLine.HostAgain(host.Folder, host.CommandLine);

        // The same gesture a session gets, and the same timeout. A host that does not take
        // it is left running: nothing has been closed, so nothing has been lost.
        if (await SessionCloser.QuitAsync(host.Pid) is { } why) return RestartResult.Fail(why);

        await Task.Delay(600);   // let the shell finish repainting its prompt

        if (!await Task.Run(() => ConsoleInput.SendLine(shell, line)))
            return RestartResult.Fail($"it quit, but the relaunch did not go in — type: {line}");

        var back = await WaitForNewHost(host, ReturnTimeout);
        if (back is null)
            return RestartResult.Fail(
                "it quit and was relaunched, but no new host is running in the folder yet — check its terminal");

        return RestartResult.Done($"relaunched as pid {back.Pid} — give it a moment to connect");
    }

    /// <summary>
    /// A host in the folder whose pid is not the one we just quit.
    ///
    /// The old one may still be on its way out when this starts looking — the quit was
    /// acknowledged, not necessarily finished — so a host with the old pid is not the answer,
    /// and only a different pid working in the same folder means the relaunch took.
    /// </summary>
    private static async Task<RemoteControlHost?> WaitForNewHost(RemoteControlHost old, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);

            var back = await Task.Run(() => RemoteControlHosts.Running()
                .FirstOrDefault(h => h.Pid != old.Pid && RemoteControlHosts.SameFolder(h.Folder, old.Folder)));
            if (back is not null) return back;
        }
        return null;
    }
}
