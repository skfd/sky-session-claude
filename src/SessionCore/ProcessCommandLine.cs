using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionCore;

/// <summary>
/// The command line a running process was started with.
///
/// The registry is the reliable map from session to process, but only for a session that
/// got far enough to write one. A CLI that starts and then hangs before registering is
/// invisible to it — running, holding a terminal, and reported by every verb as "not open
/// in a terminal". The command line is the second source of truth: a resumed session
/// carries <c>--resume &lt;id&gt;</c> on it from the moment the process exists, before any
/// file is written and whether or not startup ever completes.
///
/// Read straight out of the process's own memory (PEB → process parameters), because
/// Win32_Process would mean taking a WMI dependency and paying a full-table query for one
/// answer. Anything that fails — a process that exited mid-read, one belonging to another
/// user, a 32-bit image — returns null, which every caller reads as "no answer" rather
/// than "no match".
/// </summary>
public static class ProcessCommandLine
{
    /// <summary>The full command line of <paramref name="pid"/>, or null if it cannot be read.</summary>
    public static string? Of(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, ref info,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (info.PebBaseAddress == IntPtr.Zero) return null;

            // PEB.ProcessParameters, then RTL_USER_PROCESS_PARAMETERS.CommandLine.
            if (ReadPointer(handle, info.PebBaseAddress + PebProcessParameters) is not { } parameters) return null;
            return ReadUnicodeString(handle, parameters + ParametersCommandLine);
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// True when the command line of <paramref name="pid"/> resumes <paramref name="sessionId"/>.
    /// The id is matched as a whole token, so one session's id cannot match inside another's.
    /// </summary>
    public static bool Resumes(int pid, string sessionId) => Mentions(Of(pid), sessionId);

    /// <summary>The match rule on its own, so it can be tested without a process to inspect.</summary>
    public static bool Mentions(string? commandLine, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(commandLine)) return false;

        int at = 0;
        while ((at = commandLine.IndexOf(sessionId, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = at + sessionId.Length;
            bool startsClean = at == 0 || !IsIdChar(commandLine[at - 1]);
            bool endsClean = end == commandLine.Length || !IsIdChar(commandLine[end]);
            if (startsClean && endsClean) return true;
            at = end;
        }
        return false;
    }

    private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '-';

    /// <summary>
    /// Everything after the executable on a command line — the flags a process was started
    /// with, ready to be typed after a bare <c>claude</c>.
    ///
    /// Windows puts the image path first, quoted when it contains spaces and bare when it
    /// does not, so the only thing this has to get right is where that first token ends.
    /// Null when there is nothing to read; empty when the process was started with no
    /// arguments at all, which is a different answer and one a caller acts on differently.
    /// </summary>
    public static string? ArgumentsOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var text = commandLine.TrimStart();

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close < 0 ? null : text[(close + 1)..].Trim();
        }

        int space = text.IndexOf(' ');
        return space < 0 ? "" : text[(space + 1)..].Trim();
    }

    /// <summary>
    /// Every running claude process whose command line resumes <paramref name="sessionId"/>,
    /// oldest first — so when more than one is holding the session, the one that has held it
    /// longest is dealt with first.
    /// </summary>
    public static List<int> ResumingPids(string sessionId)
    {
        var found = new List<(int Pid, DateTime Started)>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!ClaudeInstall.IsClaudeProcess(process.ProcessName)) continue;
                if (!Resumes(process.Id, sessionId)) continue;
                found.Add((process.Id, process.StartTime));
            }
            catch { /* exited, or not ours to inspect */ }
            finally { process.Dispose(); }
        }

        return found.OrderBy(f => f.Started).Select(f => f.Pid).ToList();
    }

    // --- reading another process's memory -----------------------------------

    private static IntPtr? ReadPointer(IntPtr handle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out var read) || read != buffer.Length)
            return null;
        return new IntPtr(BitConverter.ToInt64(buffer, 0));
    }

    /// <summary>A UNICODE_STRING at <paramref name="address"/>: length, capacity, then a pointer.</summary>
    private static string? ReadUnicodeString(IntPtr handle, IntPtr address)
    {
        var header = new byte[16];
        if (!ReadProcessMemory(handle, address, header, header.Length, out var got) || got != header.Length)
            return null;

        int length = BitConverter.ToUInt16(header, 0);
        var buffer = new IntPtr(BitConverter.ToInt64(header, 8));
        if (length == 0 || buffer == IntPtr.Zero) return null;
        if (length > MaxCommandLineBytes) length = MaxCommandLineBytes;

        var text = new byte[length];
        if (!ReadProcessMemory(handle, buffer, text, text.Length, out var read) || read != text.Length)
            return null;

        return System.Text.Encoding.Unicode.GetString(text);
    }

    // --- P/Invoke -----------------------------------------------------------

    private const int ProcessBasicInformation = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    // x64 layout. The app, the CLI and the sessions they inspect are all x64; a 32-bit
    // target would read nonsense here, which is why nothing below is trusted blindly.
    private static readonly IntPtr PebProcessParameters = new(0x20);
    private static readonly IntPtr ParametersCommandLine = new(0x70);

    /// <summary>Windows caps a command line well below this; the bound is only a sanity rail.</summary>
    private const int MaxCommandLineBytes = 64 * 1024;

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
