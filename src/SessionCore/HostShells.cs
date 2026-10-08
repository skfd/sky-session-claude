namespace SessionCore;

/// <summary>
/// A shell that was opened to run a <c>claude rc</c> host and is now running nothing: the
/// host exited and the <c>-NoExit</c> PowerShell under it stayed, sitting at a prompt in a
/// tab still titled after the project.
/// </summary>
public sealed record DeadHostShell
{
    /// <summary>The PowerShell — what a relaunch or an <c>exit</c> is typed into.</summary>
    public required int Pid { get; init; }

    /// <summary>Where the shell is standing, which is the folder the host was serving.</summary>
    public required string Folder { get; init; }

    /// <summary>
    /// The <c>claude rc …</c> line the shell was opened to run, so a relaunch puts back the
    /// same host rather than standby's idea of one.
    /// </summary>
    public required string HostCommand { get; init; }

    public string Project => Standby.ProjectOf(Folder);
}

/// <summary>
/// The tabs standby left behind when their hosts went away.
///
/// A host is not the tab's root process: standby opens <c>powershell -NoExit -Command
/// "claude rc …"</c>, so when the host exits — a network drop, a sleep, whatever took the
/// connection — the shell is left at a prompt and the tab stays up, named after the project
/// and serving nothing. From the tab strip it looks exactly like a live one. Nine of them
/// sat in the standby window for five days while the phone showed no projects at all, and
/// the next Standby press added seven new tabs next to the corpses.
///
/// Read from the process table, the same way <see cref="RemoteControlHosts"/> reads a live
/// host: a PowerShell whose own command line is a host's and that has no child left. Its
/// working directory is the folder, because standby opened the tab there.
/// </summary>
public static class HostShells
{
    /// <summary>
    /// Every dead host shell, one snapshot of the process table.
    ///
    /// A shell with any child but its console host is not dead — it is running something,
    /// the host itself or whatever was typed at it afterwards — and is left alone. The
    /// questions asked of each pid are injectable for the same reason as in
    /// <see cref="RemoteControlHosts.Running"/>, and a pid that will not answer is not one.
    /// </summary>
    public static IReadOnlyList<DeadHostShell> Dead(
        IReadOnlyDictionary<int, List<ProcRef>>? children = null,
        Func<int, string?>? commandLine = null,
        Func<int, string?>? workingDirectory = null)
    {
        var tree = children ?? ProcessTree.Snapshot().Children;
        var command = commandLine ?? ProcessCommandLine.Of;
        var cwd = workingDirectory ?? ProcessCommandLine.CurrentDirectoryOf;
        var found = new List<DeadHostShell>();

        foreach (var shell in tree.Values.SelectMany(kids => kids).Where(p => IsPowerShell(p.Name)))
        {
            if (tree.TryGetValue(shell.Pid, out var kids) && kids.Any(k => !IsConsoleHost(k.Name))) continue;

            if (HostCommandIn(command(shell.Pid)) is not { } host) continue;
            if (cwd(shell.Pid) is not { Length: > 0 } folder) continue;

            found.Add(new DeadHostShell
            {
                Pid = shell.Pid,
                Folder = folder.Replace('/', '\\').TrimEnd('\\'),
                HostCommand = host,
            });
        }

        return found;
    }

    /// <summary>
    /// The <c>claude rc …</c> a PowerShell was started to run, or null when it was started for
    /// anything else.
    ///
    /// Both shapes standby has opened are read: the tab's bare <c>claude rc …</c>, and the
    /// separate window's <c>cd '…'; claude rc …</c> where Windows Terminal is missing. Only
    /// the last statement counts, and it has to be the <c>rc</c> verb as a whole token
    /// (<see cref="RemoteControlHosts.IsHostCommand"/>) — a shell opened for an ordinary
    /// session is somebody's terminal, and closing it is not ours to do.
    /// </summary>
    public static string? HostCommandIn(string? shellCommandLine)
    {
        if (ProcessCommandLine.ArgumentsOf(shellCommandLine) is not { Length: > 0 } arguments) return null;

        var at = IndexOfCommandFlag(arguments);
        if (at < 0) return null;

        var script = arguments[at..].Trim();
        if (script.Length >= 2 && script[0] == '"' && script[^1] == '"') script = script[1..^1];
        script = script.Replace("\\\"", "\"");

        var last = script.Split(';').Last().Trim();
        if (!last.StartsWith("claude ", StringComparison.OrdinalIgnoreCase)) return null;

        return RemoteControlHosts.IsHostCommand(last["claude ".Length..].Trim()) ? last : null;
    }

    /// <summary>Where the script after <c>-Command</c> (or <c>-c</c>) starts, or -1.</summary>
    private static int IndexOfCommandFlag(string arguments)
    {
        foreach (var flag in new[] { "-Command ", "-c " })
        {
            var at = arguments.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (at == 0 || arguments[at - 1] == ' ')) return at + flag.Length;
        }
        return -1;
    }

    private static bool IsPowerShell(string name) =>
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsoleHost(string name) =>
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase);
}
