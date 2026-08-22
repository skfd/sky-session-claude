using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using SessionCore;

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
///
/// Windows Terminal is one process behind every window and tab, so the process tree
/// stops at "one of these nine windows" - and each pane's shell hangs directly off
/// <c>WindowsTerminal.exe</c>, with that pane's <c>OpenConsole.exe</c> a sibling
/// rather than its parent, so no tree edge names the tab either. The title closes
/// that last gap: attaching to the session's console reads back the title the CLI
/// painted, and UI Automation finds the tab wearing it.
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
    /// Bring the terminal showing <paramref name="pid"/> to the foreground, switching
    /// to its tab when the host is tabbed. Returns false if no visible host window can
    /// be resolved (caller should then fall back to opening a fresh terminal).
    /// </summary>
    public static bool TryFocus(int pid)
    {
        var target = ResolveTarget(pid);
        if (target.Hwnd == IntPtr.Zero) return false;

        TrySelect(target.Tab);          // no-op unless the host has tabs
        return Activate(target.Hwnd);
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

    /// <summary>The window to raise, and the tab inside it to switch to (if any).</summary>
    private readonly record struct FocusTarget(IntPtr Hwnd, AutomationElement? Tab);

    // The terminal window that shows a console app is owned either by an ancestor
    // (Windows Terminal hosts the shell several levels up) or by a conhost/
    // OpenConsole child of the shell (the classic console window). So the candidate
    // owners are the process's ancestors plus each ancestor's console-host children;
    // the first that owns any visible top-level window is the host.
    //
    // Under Windows Terminal that one host owns every window at once, so "the first
    // window" is a coin toss - the title is what picks the right one out.
    private static FocusTarget ResolveTarget(int pid)
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
            if (windows.TryGetValue(candidate, out var hwnds) && hwnds.Count > 0)
                return PickTab(hwnds, ConsoleTitle(pid));

        return default;
    }

    // Find the window and tab wearing this session's title. Falling back to the first
    // window keeps the old behaviour whenever the title is unreadable or ambiguous:
    // the wrong tab of the right window still beats spawning a duplicate session.
    private static FocusTarget PickTab(List<IntPtr> hwnds, string title)
    {
        if (TerminalTitle.Topic(title).Length > 0)
            foreach (var hwnd in hwnds)
                if (MatchingTab(hwnd, title) is { } tab)
                    return new FocusTarget(hwnd, tab);

        return new FocusTarget(hwnds[0], null);
    }

    // The one tab of this window that names the session - null if none does, or if
    // several do (two idle sessions both sit under the title "Claude Code", and
    // guessing between them would drag the user somewhere they did not ask to go).
    private static AutomationElement? MatchingTab(IntPtr hwnd, string title)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            if (window is null) return null;

            var tabs = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            AutomationElement? found = null;
            foreach (AutomationElement tab in tabs)
            {
                if (!TerminalTitle.SameSession(tab.Current.Name, title)) continue;
                if (found is not null) return null;      // ambiguous
                found = tab;
            }
            return found;
        }
        catch { return null; }   // window closed mid-walk, or it exposes no automation tree
    }

    private static void TrySelect(AutomationElement? tab)
    {
        if (tab is null) return;
        try
        {
            if (true.Equals(tab.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty))) return;
            if (tab.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern pattern)
                pattern.Select();
        }
        catch { /* tab closed, or the host cannot select - raise the window regardless */ }
    }

    // --- console title ------------------------------------------------------

    // The title the CLI painted on its terminal, read by borrowing the session's own
    // console. A process may be attached to one console at a time, hence the lock; we
    // are a WPF app and own none, so there is nothing to lose by detaching after.
    private static readonly object ConsoleGate = new();

    private static string ConsoleTitle(int pid)
    {
        lock (ConsoleGate)
        {
            try
            {
                FreeConsole();                          // drop anything a previous read left attached
                if (!AttachConsole((uint)pid)) return "";
                try
                {
                    var buffer = new StringBuilder(1024);
                    GetConsoleTitle(buffer, (uint)buffer.Capacity);
                    return buffer.ToString();
                }
                finally { FreeConsole(); }
            }
            catch { return ""; }
        }
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

    // Every visible, titled, top-level window, grouped by owning pid - all of them,
    // because one Windows Terminal process owns all of its windows and only the tab
    // titles say which is which.
    private static Dictionary<int, List<IntPtr>> TopLevelWindowsByPid()
    {
        var byPid = new Dictionary<int, List<IntPtr>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;   // owned pop-up, not a main window
            if (GetWindowTextLength(hwnd) == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            if (!byPid.TryGetValue((int)pid, out var list)) byPid[(int)pid] = list = new List<IntPtr>();
            list.Add(hwnd);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetConsoleTitle(StringBuilder title, uint size);

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
