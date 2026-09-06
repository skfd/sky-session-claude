using System.Diagnostics;

namespace SessionCore;

/// <summary>
/// A live <c>claude rc</c> host: the process, and the folder it is serving.
///
/// A host is invisible everywhere Sky normally looks. It publishes no entry in
/// <c>~/.claude/sessions</c> — only the conversations it spawns do, and those are
/// <c>sdk-cli</c> and come and go — and the one file Claude Code writes for it,
/// <c>bridge-pointer.json</c>, names a session, so a host started without a pre-created
/// session (every host standby opens) never writes one at all. What is left is the process:
/// a running claude whose command line is the <c>rc</c> verb, and whose working directory
/// is the folder. That is where everything here is read from.
/// </summary>
public sealed record RemoteControlHost
{
    public required int Pid { get; init; }

    /// <summary>
    /// The repo it is serving — its working directory, as Windows keeps it, minus the
    /// trailing backslash. The same path standby opened it in.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// The process image as it now stands, which is the whole staleness signal: an update
    /// renames it (see <see cref="ClaudeInstall.IsSuperseded"/>).
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>What it was started with, so a restart can put back the same host.</summary>
    public string? CommandLine { get; init; }

    /// <summary>
    /// When the process started — a host's only answer to "how long has this been up", and
    /// the thing that explains its staleness: builds have landed since. Null when the process
    /// would not say.
    /// </summary>
    public DateTime? Started { get; init; }

    public string Project => Standby.ProjectOf(Folder);

    /// <summary>Running a build that has since been replaced.</summary>
    public bool Stale => ClaudeInstall.IsSuperseded(ProcessName);
}

/// <summary>
/// Which folders have a <c>claude rc</c> host answering for them right now.
///
/// Read off the process table, not off anything on disk. It used to be the pointer file,
/// believed only when its pid was still a running claude — and that was right as far as it
/// went, but the file is written for a session, not for a host: a host with no session yet
/// writes nothing, and three of the twenty-two hosts up on this machine when this was
/// written had no pointer under them at all. Launching a second host into a folder that
/// already has one is the mistake this exists to prevent, and it cannot be prevented from a
/// record that is sometimes not there.
/// </summary>
public static class RemoteControlHosts
{
    /// <summary>
    /// Every live host, one snapshot of the process table.
    ///
    /// The three questions asked of each process are injectable so the sieve can be tested
    /// without one: what image a pid is running, what it was started with, and where. The
    /// defaults read the real thing. A pid that will not answer any of them is not a host —
    /// a process that exited mid-inspection, or one not ours to look at — which is the safe
    /// reading: nothing is opened on the strength of a "no".
    /// </summary>
    public static IReadOnlyList<RemoteControlHost> Running(
        IEnumerable<ProcRef>? processes = null,
        Func<int, string?>? commandLine = null,
        Func<int, string?>? workingDirectory = null,
        Func<int, DateTime?>? startedAt = null)
    {
        var command = commandLine ?? ProcessCommandLine.Of;
        var cwd = workingDirectory ?? ProcessCommandLine.CurrentDirectoryOf;
        var started = startedAt ?? StartedAt;
        var found = new List<RemoteControlHost>();

        foreach (var process in processes ?? LiveClaudes())
        {
            if (!IsClaudeImage(process.Name)) continue;

            var line = command(process.Pid);
            if (!IsHostCommand(ProcessCommandLine.ArgumentsOf(line))) continue;

            if (cwd(process.Pid) is not { Length: > 0 } folder) continue;

            found.Add(new RemoteControlHost
            {
                Pid = process.Pid,
                Folder = folder.Replace('/', '\\').TrimEnd('\\'),
                ProcessName = process.Name,
                CommandLine = line,
                Started = started(process.Pid),
            });
        }

        return found;
    }

    /// <summary>The host serving <paramref name="folder"/>, from a list already taken, or null.</summary>
    public static RemoteControlHost? ServingFolder(IEnumerable<RemoteControlHost> hosts, string folder) =>
        hosts.FirstOrDefault(h => SameFolder(h.Folder, folder));

    /// <summary>The host serving <paramref name="folder"/> right now, or null.</summary>
    public static RemoteControlHost? ServingFolder(string folder) => ServingFolder(Running(), folder);

    /// <summary>
    /// Whether arguments read off a process are a host's. <c>rc</c> has to be the whole first
    /// token: a future <c>rcx</c> is not this verb, and relaunching it as one would be worse
    /// than admitting the command line was not understood.
    /// </summary>
    public static bool IsHostCommand(string? arguments) =>
        arguments is { Length: > 0 }
        && (arguments.Equals("rc", StringComparison.OrdinalIgnoreCase)
            || arguments.StartsWith("rc ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The running claude processes, by <see cref="Process.ProcessName"/> rather than the tree
    /// snapshot's image name, because that is the rename-aware form staleness is read from
    /// (see <see cref="ClaudeInstall.IsSuperseded"/>).
    /// </summary>
    private static IEnumerable<ProcRef> LiveClaudes()
    {
        foreach (var process in Process.GetProcesses())
        {
            int pid;
            string? name;
            try { pid = process.Id; name = process.ProcessName; }
            catch { continue; }                 // exited between the snapshot and the question
            finally { process.Dispose(); }

            if (ClaudeInstall.IsClaudeProcess(name))
                yield return new ProcRef(pid, name);
        }
    }

    /// <summary>
    /// The image name of a running process, or null when it is gone or not ours to look at.
    /// <see cref="Process.ProcessName"/> rather than WMI on purpose — see
    /// <see cref="ClaudeInstall.IsSuperseded"/>.
    /// </summary>
    public static string? NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>When a process started, or null when it is gone or will not say.</summary>
    public static DateTime? StartedAt(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch { return null; }
    }

    /// <summary>
    /// How many claude processes sit directly under a host — its conversations, as the
    /// process tree sees them.
    ///
    /// Counted here rather than from the registry because the difference between the two
    /// counts is the whole point: a conversation that has spawned but not yet published a
    /// registry entry shows up in one and not the other, and that gap is what
    /// <see cref="HostRestartPolicy"/> refuses to sweep through.
    ///
    /// Image names in a tree snapshot are the file on disk (<c>claude.exe</c>), not
    /// <see cref="Process.ProcessName"/>'s rename-aware form, so the test is its own.
    /// </summary>
    public static int ConversationsUnder(int pid, IReadOnlyDictionary<int, List<ProcRef>> children) =>
        children.TryGetValue(pid, out var kids)
            ? kids.Count(kid => IsClaudeImage(kid.Name))
            : 0;

    /// <summary>A claude by either spelling: the file on disk, or the process name an update has renamed.</summary>
    private static bool IsClaudeImage(string? name) =>
        name is not null
        && (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
            || ClaudeInstall.IsClaudeProcess(name));

    /// <summary>
    /// The hosts a folder or a project name picks out.
    ///
    /// Narrowest first: a whole folder, then a project name in full, and only then a
    /// substring. A repo whose name contains another's would otherwise shadow it —
    /// <c>xrm-ribbon</c> is exactly <c>xrm-ribbon</c> before it is one of the <c>xrm</c>
    /// ones — and the caller quits a server with this, so an ambiguous answer has to come
    /// back ambiguous rather than resolved by luck.
    /// </summary>
    public static IReadOnlyList<RemoteControlHost> Matching(
        IEnumerable<RemoteControlHost> hosts, string wanted)
    {
        var all = hosts.ToList();
        var folder = wanted.Replace('/', '\\').TrimEnd('\\');

        return Narrow(h => SameFolder(h.Folder, folder))
            ?? Narrow(h => string.Equals(h.Project, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Narrow(h => h.Project.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            ?? [];

        List<RemoteControlHost>? Narrow(Func<RemoteControlHost, bool> match)
        {
            var hit = all.Where(match).ToList();
            return hit.Count > 0 ? hit : null;
        }
    }

    /// <summary>
    /// The live conversations a host is answering for.
    ///
    /// Two ways in, because either alone has a hole. The process tree is the precise answer —
    /// a host spawns its conversations, so they are its children — but it only holds while
    /// they are spawned directly; put anything in between and a host serving a busy
    /// conversation would read as serving nothing. So an <c>sdk-cli</c> session sitting in the
    /// host's own folder counts too, whoever launched it: that is what a host's conversations
    /// are, and a bridged terminal in the same folder is <c>cli</c> and stays out of it.
    ///
    /// Erring towards counting one that is not a host's costs a skipped restart. Erring the
    /// other way costs someone's turn, mid-flight, on a phone we cannot see.
    /// </summary>
    public static IEnumerable<LiveSession> Serving(
        RemoteControlHost host,
        IEnumerable<LiveSession> live,
        IReadOnlyDictionary<int, int> parents) =>
        live.Where(session =>
            (parents.TryGetValue(session.Pid, out var parent) && parent == host.Pid)
            || (string.Equals(session.Entrypoint, "sdk-cli", StringComparison.OrdinalIgnoreCase)
                && SameFolder(session.Cwd, host.Folder)));

    /// <summary>
    /// How two spellings of the same folder are told to be the same folder: either slash,
    /// with or without the trailing one, in any case. A process's working directory ends in
    /// a backslash and a transcript's cwd does not, and both are the folder.
    /// </summary>
    public static bool SameFolder(string? one, string? other) =>
        one is { Length: > 0 } && other is { Length: > 0 }
        && string.Equals(
            one.Replace('/', '\\').TrimEnd('\\'),
            other.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
