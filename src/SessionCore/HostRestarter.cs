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
/// and what confirms it came back is <c>bridge-pointer.json</c> naming a new pid — the file
/// is how a host says it is serving a folder, and the only way to hear it say so.
/// </summary>
public static class HostRestarter
{
    /// <summary>
    /// How long to wait for a host to claim the folder again. Longer than a session's, since
    /// a host has to connect to the account and pre-create a session before it writes.
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
                "it quit and was relaunched, but no host has claimed the folder yet — check its terminal");

        var note = $"back as pid {back.Pid}";
        return RestartResult.Done(
            back.SessionId == host.BridgeSessionId ? $"{note}, on the same bridge session" : note);
    }

    /// <summary>
    /// The folder's pointer naming a pid that is not the one we just quit.
    ///
    /// Existence is not the test: the pointer outlives its host, so the dead one's file is
    /// still sitting there and would answer immediately. Only a changed pid means a new host
    /// got far enough to serve the folder.
    /// </summary>
    private static async Task<BridgePointer?> WaitForNewHost(RemoteControlHost old, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);

            if (await Task.Run(() => BridgePointer.Read(old.ProjectDir)) is { } pointer
                && pointer.Pid != old.Pid)
                return pointer;
        }
        return null;
    }
}
