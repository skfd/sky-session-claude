using System.Diagnostics;

namespace SessionCore;

/// <summary>
/// Finds Claude sessions that are open <em>right now</em> in a terminal, and works out
/// which shell each one would hand its terminal back to.
///
/// Every running interactive CLI publishes a registry file at
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> holding its <c>sessionId</c> (which for a
/// local session is exactly the <c>.jsonl</c> base name — our
/// <see cref="SessionInfo.SessionId"/>) and its pid. That is the only reliable map from
/// session to process: the id is generated at runtime and appears on no command line and
/// in no creation-time environment block.
///
/// This half is pure Win32 and file reads, so it works headlessly — the CLI restarts
/// sessions with it. Raising and switching to the window needs UI Automation and lives in
/// the app instead (<c>SessionWindows</c>), which is the only part that wants a desktop.
/// </summary>
public static class LiveSessions
{
    /// <summary>
    /// Session id → every interactive CLI currently running it. A session normally maps to
    /// one process; the list guards the rare duplicate (two terminals resumed the same id)
    /// so a caller can try each.
    /// </summary>
    public static Dictionary<string, List<LiveSession>> Scan()
    {
        var map = new Dictionary<string, List<LiveSession>>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in LiveSessionRegistry.Read(LiveSessionRegistry.DefaultDir()))
        {
            // Only interactive terminals have a console we can drive or a window to focus.
            if (!string.Equals(session.Kind, "interactive", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsLiveClaude(session.Pid)) continue;   // skip stale registry files

            if (!map.TryGetValue(session.SessionId, out var running))
                map[session.SessionId] = running = new List<LiveSession>();
            if (!running.Any(r => r.Pid == session.Pid)) running.Add(session);
        }

        return map;
    }

    /// <summary>Every live session as a flat list, newest registry entry first.</summary>
    public static List<LiveSession> All() =>
        Scan().Values.SelectMany(v => v).ToList();

    /// <summary>The live session running <paramref name="sessionId"/>, or null.</summary>
    public static LiveSession? Find(string sessionId) =>
        Scan().TryGetValue(sessionId, out var running) && running.Count > 0 ? running[0] : null;

    /// <summary>
    /// The shell that will still be there once the session at <paramref name="pid"/> quits —
    /// the process the terminal hands control back to, and so the one to type the resume
    /// command at.
    ///
    /// Null when nothing survives: a Claude started as the tab's own root process takes the
    /// tab down with it, and a shell we cannot write a PowerShell command line to is no
    /// better than none. Either way the caller must open a fresh terminal instead.
    /// </summary>
    public static int? ShellFor(int pid)
    {
        var (parents, children) = ProcessTree.Snapshot();
        var names = ProcessTree.NamesOf(children);

        int cur = pid;
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 16 && cur != 0 && seen.Add(cur); depth++)
        {
            if (!parents.TryGetValue(cur, out int parent) || parent == 0) return null;

            var name = names.TryGetValue(parent, out var n) ? n : "";
            if (IsPowerShell(name)) return parent;
            if (!IsClaudeLayer(name)) return null;   // terminal host, explorer, cmd.exe, ...

            cur = parent;
        }
        return null;
    }

    // The launcher and its node runtime sit between the session and its shell.
    private static bool IsClaudeLayer(string name) =>
        name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node.exe", StringComparison.OrdinalIgnoreCase);

    // PowerShell only: the relaunch is one PowerShell command line, and cmd.exe would
    // read its "cd 'x'; claude ..." as something else entirely.
    private static bool IsPowerShell(string name) =>
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    // A registry file can outlive its process; confirm the pid is a running claude before
    // trusting it, so we never drive or focus a window that reused the pid.
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
