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

    private static bool IsLiveClaude(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return ClaudeInstall.IsClaudeProcess(p.ProcessName);
        }
        catch { return false; }
    }
}
