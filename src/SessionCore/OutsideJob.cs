using System.Management;
using System.Runtime.InteropServices;

namespace SessionCore;

/// <summary>
/// Start a process that does not die with whatever started us.
///
/// A process started from an agent's shell under the Claude desktop app died when the app
/// quit: that is what killed the dev-build Sky and its keeper on 2026-10-08. The same would
/// hold for every terminal that <c>SessionCli new</c>, <c>resume</c> or <c>fork</c> opens from
/// such a shell. The app's own job is not what holds them: it is a broad one shared with
/// browsers and terminal sessions (looked at 2026-11-09). Whatever holds them hangs off the
/// session the app starts, the agent's process tree or a job of its own.
///
/// <c>Win32_Process.Create</c> starts the process from the WMI host instead. It is the
/// host's child, not ours, so it is in neither our process tree nor any job of ours, which
/// is what <c>publish.ps1</c> already does when it relaunches Sky.
///
/// The switch is the desktop app, not job membership. Explorer, Windows Terminal and Sky all
/// sit in a job of some kind (<c>IsProcessInJob</c> says yes for every one of them), and
/// those jobs let children go. So WMI is used only when this process has a package identity
/// or descends from the app (see <see cref="Reason"/>). Everywhere else the launch stays a
/// plain <c>Process.Start</c>, because a process WMI creates may open behind the window you
/// are looking at, and a double-click in Sky should bring its terminal to the front.
///
/// <c>SessionCli doctor</c> reports which way this process would launch, and starts one
/// windowless process through WMI to prove the published exe still can.
/// </summary>
public static class OutsideJob
{
    private const int NoPackage = 15700;   // APPMODEL_ERROR_NO_PACKAGE

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, char[]? name);

    /// <summary>Where the desktop app's own executable lives, as a fragment of its path.</summary>
    private const string ClaudeAppFolder = @"\WindowsApps\Claude_";

    private static readonly Lazy<string?> reason = new(FindReason);

    /// <summary>
    /// Why a launch from this process has to go through WMI, or null when it does not. Asked
    /// once per process: neither a package nor a line of ancestors changes while it runs.
    /// </summary>
    public static string? Reason => reason.Value;

    /// <summary>Whether a launch from this process must go through WMI to outlive its starter.</summary>
    public static bool Needed => Reason is not null;

    /// <summary>
    /// Two ways to be held, either one enough. Package identity is checked first because it
    /// costs nothing, but it does not carry far: a process started in the Claude package has
    /// it, and that process's own children (through <c>cmd</c> or PowerShell) do not. An
    /// agent's <c>SessionCli</c> is several generations down, so descending from the app's own
    /// <c>claude.exe</c> is the check that actually fires there.
    /// </summary>
    private static string? FindReason()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (PackageName() is { } package) return $"it runs as the package {package}";
            return ClaudeAppAncestor(ProcessTable(), Environment.ProcessId) is { } app
                ? $"it was started by the Claude desktop app ({app})"
                : null;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException
                                      or EntryPointNotFoundException or TypeInitializationException)
        {
            return null;   // a WMI that cannot list processes cannot start them either
        }
    }

    private static string? PackageName()
    {
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) == NoPackage) return null;
        var name = new char[Math.Max(length, 1)];
        return GetCurrentPackageFullName(ref length, name) == 0
            ? new string(name, 0, (int)Math.Max(length, 1) - 1)
            : "(unnamed)";
    }

    /// <summary>One process as the ancestry walk needs it.</summary>
    internal readonly record struct ProcessEntry(int Parent, string? Path, DateTime Created);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<int, ProcessEntry> ProcessTable()
    {
        var table = new Dictionary<int, ProcessEntry>();
        using var search = new ManagementObjectSearcher(
            @"root\cimv2", "SELECT ProcessId, ParentProcessId, ExecutablePath, CreationDate FROM Win32_Process");
        using var results = search.Get();
        foreach (var row in results)
            using (row)
            {
                var created = row["CreationDate"] is string stamp
                    ? ManagementDateTimeConverter.ToDateTime(stamp)
                    : DateTime.MinValue;
                table[(int)Convert.ToUInt32(row["ProcessId"])] = new ProcessEntry(
                    (int)Convert.ToUInt32(row["ParentProcessId"]), row["ExecutablePath"] as string, created);
            }
        return table;
    }

    /// <summary>
    /// The desktop app's executable among <paramref name="pid"/>'s ancestors, or null. A
    /// parent id outlives its process and can be handed to a stranger, so a "parent" started
    /// after its child is not one, and the walk stops there — as it does at a loop.
    /// </summary>
    internal static string? ClaudeAppAncestor(IReadOnlyDictionary<int, ProcessEntry> table, int pid)
    {
        var seen = new HashSet<int> { pid };
        if (!table.TryGetValue(pid, out var child)) return null;

        while (table.TryGetValue(child.Parent, out var parent) && seen.Add(child.Parent))
        {
            if (parent.Created > child.Created) return null;
            if (parent.Path is { } path
                && path.Contains(ClaudeAppFolder, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
                return path;
            child = parent;
        }
        return null;
    }

    /// <summary>
    /// Start <paramref name="commandLine"/> from the WMI host, in <paramref name="folder"/>,
    /// with its own window hidden when <paramref name="hidden"/> is set. Returns the new
    /// process's id, or null when WMI could not start it — the caller then starts it the
    /// ordinary way, since a process that dies with the app is better than no process.
    ///
    /// What it inherits is not ours: the environment is the user's default one from the
    /// registry, and no handles cross. Both are what <see cref="TerminalLauncher"/> already
    /// arranges for itself, so the terminal it opens is the same either way.
    /// </summary>
    public static int? Start(string commandLine, string? folder, bool hidden)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var process = new ManagementClass(@"root\cimv2", "Win32_Process", null);
            using var startupClass = new ManagementClass(@"root\cimv2", "Win32_ProcessStartup", null);
            using var startup = startupClass.CreateInstance();
            if (hidden) startup["ShowWindow"] = (ushort)0;   // SW_HIDE

            using var input = process.GetMethodParameters("Create");
            input["CommandLine"] = commandLine;
            input["ProcessStartupInformation"] = startup;
            if (folder is { Length: > 0 } && Directory.Exists(folder)) input["CurrentDirectory"] = folder;

            using var output = process.InvokeMethod("Create", input, null);
            return Convert.ToUInt32(output["ReturnValue"]) == 0
                ? (int)Convert.ToUInt32(output["ProcessId"])
                : null;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or TypeInitializationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>cmd</c> by its full path, so a launch through WMI does not depend on the WMI host's
    /// PATH, which is not ours.
    /// </summary>
    public static string CommandShell =>
        Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec
            ? comspec
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Whether WMI will start a process right now: one windowless <c>cmd /c exit</c>, for the
    /// doctor, which runs the published exe the agents run.
    /// </summary>
    public static bool Probe() => Start($"\"{CommandShell}\" /c exit", null, hidden: true) is not null;

    /// <summary>
    /// The id of the process that started <paramref name="pid"/>, or null if it is gone or
    /// cannot be read. For the tests, which tell a WMI-hosted launch from one of ours by it.
    /// </summary>
    internal static int? ParentOf(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var search = new ManagementObjectSearcher(
            @"root\cimv2", $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
        using var results = search.Get();
        foreach (var row in results)
            using (row)
                return (int)Convert.ToUInt32(row["ParentProcessId"]);
        return null;
    }
}
