using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SessionApp;

/// <summary>
/// Finds Claude sessions that are open <em>right now</em> in a terminal, so a
/// double-click can jump to that live window instead of spawning a second
/// <c>claude --resume</c> against the same session.
///
/// Every running interactive CLI publishes a registry file at
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> holding its <c>sessionId</c> (which
/// for a local session is exactly the <c>.jsonl</c> base name — our
/// <see cref="SessionCore.SessionInfo.SessionId"/>) and its pid. We read that to
/// map session → pid, then walk from the pid up to the hosting terminal window
/// to focus it. (The id is generated at runtime and never appears on the command
/// line or in the process's creation-time environment, so the registry is the
/// only reliable source.)
/// </summary>
internal static class LiveSessions
{
    /// <summary>
    /// Session id → the pids of every interactive CLI currently running it.
    /// A session normally maps to one pid; the list guards the rare duplicate
    /// (e.g. two terminals resumed the same id) so focusing can try each.
    /// </summary>
    public static Dictionary<string, List<int>> Scan()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        var dir = SessionsDir();
        if (!Directory.Exists(dir)) return map;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = doc.RootElement;

                // Only interactive terminals have a window to focus.
                if (root.TryGetProperty("kind", out var kind) &&
                    kind.ValueKind == JsonValueKind.String &&
                    !string.Equals(kind.GetString(), "interactive", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!root.TryGetProperty("sessionId", out var sidEl) ||
                    sidEl.GetString() is not { Length: > 0 } sid)
                    continue;
                if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid))
                    continue;

                if (!IsLiveClaude(pid)) continue;   // skip stale registry files

                if (!map.TryGetValue(sid, out var pids)) map[sid] = pids = new List<int>();
                if (!pids.Contains(pid)) pids.Add(pid);
            }
            catch { /* unreadable/racing/partial file — skip it */ }
        }

        return map;
    }

    /// <summary>
    /// Bring the terminal window hosting <paramref name="pid"/> to the foreground.
    /// Returns false if no visible host window can be resolved (caller should then
    /// fall back to opening a fresh terminal).
    /// </summary>
    public static bool TryFocus(int pid)
    {
        var hwnd = ResolveTerminalWindow(pid);
        if (hwnd == IntPtr.Zero) return false;
        return Activate(hwnd);
    }

    private static string SessionsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    // A registry file can outlive its process; confirm the pid is a running claude
    // before trusting it, so we never focus a window that reused the pid.
    private static bool IsLiveClaude(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // --- window resolution --------------------------------------------------

    // The terminal window that shows a console app is owned either by an ancestor
    // (Windows Terminal hosts the shell several levels up) or by a conhost/
    // OpenConsole child of the shell (the classic console window). So the candidate
    // owners are the process's ancestors plus each ancestor's console-host children;
    // the first with a visible top-level window is the one to focus.
    private static IntPtr ResolveTerminalWindow(int pid)
    {
        var (parents, childrenOf) = SnapshotProcessTree();
        var windows = TopLevelWindowsByPid();

        var candidates = new List<int>();
        var seen = new HashSet<int>();
        int cur = pid;
        for (int depth = 0; depth < 16 && cur != 0 && seen.Add(cur); depth++)
        {
            candidates.Add(cur);
            if (childrenOf.TryGetValue(cur, out var kids))
                foreach (var kid in kids)
                    if (IsConsoleHost(kid.Name)) candidates.Add(kid.Pid);

            cur = parents.TryGetValue(cur, out var parent) ? parent : 0;
        }

        foreach (var candidate in candidates)
            if (windows.TryGetValue(candidate, out var hwnd)) return hwnd;

        return IntPtr.Zero;
    }

    private static bool IsConsoleHost(string name) =>
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase);

    // --- foreground activation (the documented AttachThreadInput dance) ------

    private static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint appThread = GetCurrentThreadId();

        bool attached = fgThread != appThread && AttachThreadInput(fgThread, appThread, true);
        try
        {
            BringWindowToTop(hwnd);
            bool ok = SetForegroundWindow(hwnd);
            return ok || GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, appThread, false);
        }
    }

    // --- process tree + window enumeration ----------------------------------

    private readonly record struct ProcRef(int Pid, string Name);

    private static (Dictionary<int, int> Parents, Dictionary<int, List<ProcRef>> Children) SnapshotProcessTree()
    {
        var parents = new Dictionary<int, int>();
        var children = new Dictionary<int, List<ProcRef>>();

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return (parents, children);
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return (parents, children);
            do
            {
                int pid = (int)entry.th32ProcessID;
                int ppid = (int)entry.th32ParentProcessID;
                string name = entry.szExeFile;

                parents[pid] = ppid;
                if (!children.TryGetValue(ppid, out var list))
                    children[ppid] = list = new List<ProcRef>();
                list.Add(new ProcRef(pid, name));
            }
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }

        return (parents, children);
    }

    // First visible, titled, top-level window for each owning pid.
    private static Dictionary<int, IntPtr> TopLevelWindowsByPid()
    {
        var byPid = new Dictionary<int, IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;   // owned pop-up, not a main window
            if (GetWindowTextLength(hwnd) == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) byPid.TryAdd((int)pid, hwnd);
            return true;
        }, IntPtr.Zero);
        return byPid;
    }

    // --- P/Invoke -----------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
}
