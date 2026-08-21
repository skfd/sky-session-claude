using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SessionApp;

/// <summary>
/// Finds Claude sessions that are open <em>right now</em> in a terminal, so a
/// double-click can jump to that live window instead of spawning a second
/// <c>claude --resume</c> against the same session.
///
/// A running interactive CLI carries its session id in the
/// <c>CLAUDE_CODE_SESSION_ID</c> environment variable, and for a local session
/// that id is exactly the <c>.jsonl</c> file's base name — i.e. our
/// <see cref="SessionCore.SessionInfo.SessionId"/>. We read that variable out of
/// each <c>claude.exe</c>'s PEB (same user, no elevation) to build the id→pid map,
/// then walk from the pid up to the hosting terminal window to focus it.
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

        foreach (var p in SafeGetProcessesByName("claude"))
        {
            try
            {
                var env = ReadEnvironment(p.Id);
                if (env is null) continue;

                // CLAUDECODE=1 marks the CLI; the Electron desktop app lacks it.
                if (!env.TryGetValue("CLAUDECODE", out var cc) || cc != "1") continue;
                if (!env.TryGetValue("CLAUDE_CODE_SESSION_ID", out var sid) || string.IsNullOrEmpty(sid))
                    continue;

                if (!map.TryGetValue(sid, out var pids)) map[sid] = pids = new List<int>();
                if (!pids.Contains(p.Id)) pids.Add(p.Id);
            }
            catch { /* process died mid-scan, or no read access — skip it */ }
            finally { p.Dispose(); }
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

    private static Process[] SafeGetProcessesByName(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

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

    // --- reading a remote process's environment block (PEB, x64) ------------

    private static Dictionary<string, string>? ReadEnvironment(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB.ProcessParameters (0x20) → RTL_USER_PROCESS_PARAMETERS.Environment
            // (0x80) and .EnvironmentSize (0x3F0), all x64 offsets.
            IntPtr pp = ReadPtr(h, pbi.PebBaseAddress + 0x20);
            if (pp == IntPtr.Zero) return null;

            IntPtr envAddr = ReadPtr(h, pp + 0x80);
            if (envAddr == IntPtr.Zero) return null;

            long size = ReadInt64(h, pp + 0x3F0);
            if (size <= 0 || size > 8 * 1024 * 1024) size = 128 * 1024;   // sane fallback

            var buffer = new byte[size];
            if (!ReadProcessMemory(h, envAddr, buffer, (IntPtr)size, out IntPtr read) || read == IntPtr.Zero)
                return null;

            return ParseEnvironmentBlock(buffer, (int)read);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static Dictionary<string, string> ParseEnvironmentBlock(byte[] buffer, int bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = Encoding.Unicode.GetString(buffer, 0, bytes & ~1);   // whole wchars only
        foreach (var entry in text.Split('\0'))
        {
            if (entry.Length == 0) continue;
            int eq = entry.IndexOf('=', 1);   // skip a leading '=' (drive-cwd vars like "=C:")
            if (eq <= 0) continue;
            result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    private static IntPtr ReadPtr(IntPtr h, IntPtr addr)
    {
        var buf = new byte[8];
        return ReadProcessMemory(h, addr, buf, (IntPtr)8, out _)
            ? (IntPtr)BitConverter.ToInt64(buf, 0)
            : IntPtr.Zero;
    }

    private static long ReadInt64(IntPtr h, IntPtr addr)
    {
        var buf = new byte[8];
        return ReadProcessMemory(h, addr, buf, (IntPtr)8, out _) ? BitConverter.ToInt64(buf, 0) : 0;
    }

    // --- P/Invoke -----------------------------------------------------------

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PROCESS_VM_READ = 0x0010;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

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
