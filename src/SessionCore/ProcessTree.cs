using System.Runtime.InteropServices;

namespace SessionCore;

/// <summary>One process in a tree snapshot.</summary>
public readonly record struct ProcRef(int Pid, string Name);

/// <summary>
/// A point-in-time snapshot of every process and its parent.
///
/// Two very different questions are answered by walking the same tree: which shell a
/// session will hand its terminal back to (<see cref="LiveSessions.ShellFor"/>), and which
/// window shows a session (the app's focus path). Both need parents and children, and
/// taking one snapshot for a whole walk keeps the answer self-consistent — processes come
/// and go while you enumerate them.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Every pid mapped to its parent pid, and every pid mapped to its children.
    /// Empty maps when the snapshot cannot be taken; callers treat that as "no answer",
    /// which is always the safe reading here.
    /// </summary>
    public static (Dictionary<int, int> Parents, Dictionary<int, List<ProcRef>> Children) Snapshot()
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

                parents[pid] = ppid;
                if (!children.TryGetValue(ppid, out var list))
                    children[ppid] = list = new List<ProcRef>();
                list.Add(new ProcRef(pid, entry.szExeFile));
            }
            while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }

        return (parents, children);
    }

    /// <summary>Pid → image name, flattened out of a snapshot's children map.</summary>
    public static Dictionary<int, string> NamesOf(Dictionary<int, List<ProcRef>> children)
    {
        var names = new Dictionary<int, string>();
        foreach (var kids in children.Values)
            foreach (var kid in kids)
                names[kid.Pid] = kid.Name;
        return names;
    }

    // --- P/Invoke -----------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
}
