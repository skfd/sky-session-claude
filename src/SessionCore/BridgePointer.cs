using System.Diagnostics;
using System.Text.Json;

namespace SessionCore;

/// <summary>
/// The record a <c>claude rc</c> host leaves in a project's transcript folder, naming the
/// bridge session it is serving and the process serving it.
///
/// This exists because a Remote Control host is invisible everywhere else. A host publishes
/// no entry in <c>~/.claude/sessions</c> — only the conversations it spawns do, and those are
/// <c>sdk-cli</c> and come and go — so a folder with a host and no live conversation looks,
/// from the registry, exactly like a folder with nothing running at all. Launch a second host
/// there and you get two, both answering the same phone.
///
/// <c>bridge-pointer.json</c> is what Claude Code itself uses for <c>claude rc --continue</c>,
/// so it is the one durable statement that a folder is being served. Like the session registry
/// it outlives its process — killing a host leaves the file exactly where it was — which is
/// why nothing here trusts a pointer without asking whether its pid is still a live claude.
/// </summary>
public sealed record BridgePointer
{
    /// <summary>The bridge session id — the <c>session_…</c> that claude.ai/code addresses.</summary>
    public required string SessionId { get; init; }

    /// <summary>The host process. Meaningless on its own; see <see cref="RemoteControlHosts"/>.</summary>
    public required int Pid { get; init; }

    public const string FileName = "bridge-pointer.json";

    public static string PathIn(string projectDir) => Path.Combine(projectDir, FileName);

    /// <summary>Read the pointer in a project's transcript folder, or null if there is none.</summary>
    public static BridgePointer? Read(string projectDir)
    {
        if (string.IsNullOrEmpty(projectDir)) return null;

        try
        {
            var path = PathIn(projectDir);
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path));
        }
        catch (IOException) { return null; }          // written while we read it
        catch (UnauthorizedAccessException) { return null; }
    }

    public static BridgePointer? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            // ValueKind first: TryGetInt32 throws rather than answering false when the property
            // is there but is not a number at all.
            if (!root.TryGetProperty("pid", out var pid)
                || pid.ValueKind != JsonValueKind.Number
                || !pid.TryGetInt32(out var number))
                return null;

            return new BridgePointer { SessionId = id.GetString()!, Pid = number };
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// A live host, with the facts a sweep needs on top of the pointer that found it.
///
/// A host publishes no registry entry, so none of what a session hands over for free — its
/// build, its folder, whether it is mid-turn — is there to read. This is the substitute:
/// the folder comes from the scan that led here, and the rest is read off the process.
/// </summary>
public sealed record RemoteControlHost
{
    public required BridgePointer Pointer { get; init; }

    /// <summary>The repo it was started in — from the scan, never slugged back out of the path.</summary>
    public required string Folder { get; init; }

    /// <summary>The transcript folder its pointer sits in.</summary>
    public required string ProjectDir { get; init; }

    /// <summary>
    /// The process image as it now stands, which is the whole staleness signal: an update
    /// renames it (see <see cref="ClaudeInstall.IsSuperseded"/>).
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>What it was started with, so a restart can put back the same host.</summary>
    public string? CommandLine { get; init; }

    public int Pid => Pointer.Pid;

    /// <summary>The bridge session the phone addresses — the closest thing a host has to an id.</summary>
    public string BridgeSessionId => Pointer.SessionId;

    public string Project => Standby.ProjectOf(Folder);

    /// <summary>Running a build that has since been replaced.</summary>
    public bool Stale => ClaudeInstall.IsSuperseded(ProcessName);
}

/// <summary>
/// Which folders have a <c>claude rc</c> host answering for them right now.
/// </summary>
public static class RemoteControlHosts
{
    /// <summary>
    /// The live host serving <paramref name="projectDir"/>'s folder, or null.
    /// </summary>
    /// <param name="projectDir">
    /// The transcript folder under <c>~/.claude/projects</c>, not the repo — that is where the
    /// pointer is written, and it is <see cref="Path.GetDirectoryName(string)"/> of any session
    /// file the scanner already found, so no path has to be slugged to ask this.
    /// </param>
    /// <param name="isLiveHost">
    /// Whether a pid is still a running claude. Injected for tests; the default follows what
    /// the session registry does with the same problem — a pid that exists and belongs to a
    /// claude process is trusted, and a pid reused by a *different* claude is a case the
    /// registry accepts too rather than paying for process-start comparison everywhere.
    /// </param>
    public static BridgePointer? ServingFrom(string projectDir, Func<int, bool>? isLiveHost = null)
    {
        if (BridgePointer.Read(projectDir) is not { } pointer) return null;
        return (isLiveHost ?? IsLiveClaude)(pointer.Pid) ? pointer : null;
    }

    private static bool IsLiveClaude(int pid) => ClaudeInstall.IsClaudeProcess(NameOf(pid));

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

    private static bool IsClaudeImage(string? name) =>
        name is not null
        && (name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
            || ClaudeInstall.IsSuperseded(name));

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

    private static bool SameFolder(string? one, string? other) =>
        one is { Length: > 0 } && other is { Length: > 0 }
        && string.Equals(
            one.Replace('/', '\\').TrimEnd('\\'),
            other.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every live host behind a scan, one per folder.
    ///
    /// The folder is the reason this takes sessions rather than walking
    /// <c>~/.claude/projects</c>: a transcript folder's name is a slug, and
    /// <c>skyfallsdown-com</c> cannot be turned back into <c>skyfallsdown.com</c>. The scan
    /// already carries both halves — where the session ran, and which folder its file sits
    /// in — so a host found this way always knows the real path to relaunch in.
    ///
    /// A folder whose host is not running is not here at all; the pointer outlives the
    /// process that wrote it, and <see cref="ServingFrom"/> is the check that separates them.
    /// </summary>
    public static IEnumerable<RemoteControlHost> FromScan(
        IEnumerable<SessionInfo> sessions,
        Func<int, string?>? processName = null,
        Func<int, string?>? commandLine = null)
    {
        var name = processName ?? NameOf;
        var command = commandLine ?? ProcessCommandLine.Of;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            if (session.RealCwd is not { Length: > 0 } folder) continue;
            if (string.IsNullOrEmpty(session.FilePath)) continue;
            if (!seen.Add(folder.Replace('/', '\\').TrimEnd('\\'))) continue;

            var projectDir = Path.GetDirectoryName(session.FilePath) ?? "";
            if (BridgePointer.Read(projectDir) is not { } pointer) continue;

            var image = name(pointer.Pid);
            if (!ClaudeInstall.IsClaudeProcess(image)) continue;   // the pointer outlived its host

            yield return new RemoteControlHost
            {
                Pointer = pointer,
                Folder = folder,
                ProjectDir = projectDir,
                ProcessName = image!,
                CommandLine = command(pointer.Pid),
            };
        }
    }
}
