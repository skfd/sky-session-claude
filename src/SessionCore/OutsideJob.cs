using System.Management;
using System.Runtime.InteropServices;

namespace SessionCore;

/// <summary>
/// Start a process that does not die with whatever started us.
///
/// The Claude desktop app is an MSIX package, and everything it starts inherits the
/// package's identity and its job: an agent's shell, the <c>SessionCli</c> that shell runs,
/// and every terminal that <c>SessionCli new</c>, <c>resume</c> or <c>fork</c> opens. Quitting
/// the app closes the job and Windows takes the lot down with it, so a session opened from
/// the desktop app lasted exactly as long as the app did (the same thing that took the
/// dev-build Sky and its keeper down on 2026-10-08).
///
/// <c>Win32_Process.Create</c> starts the process from the WMI host instead. It is the
/// host's child, not ours, and inherits neither our job nor our package, which is what
/// <c>publish.ps1</c> already does when it relaunches Sky.
///
/// The switch is package identity, not job membership. Explorer, Windows Terminal and Sky
/// all sit in a job of some kind (<c>IsProcessInJob</c> says yes for every one of them),
/// and those jobs let children go. Only a packaged parent holds on, and a process can ask
/// whether it has one. Everywhere else the launch stays a plain <c>Process.Start</c>,
/// because a process WMI creates may open behind the window you are looking at, and a
/// double-click in Sky should bring its terminal to the front.
/// </summary>
public static class OutsideJob
{
    private const int NoPackage = 15700;   // APPMODEL_ERROR_NO_PACKAGE

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    /// <summary>
    /// Whether this process runs with a package's identity, so that its children die with the
    /// package's app. True under the Claude desktop app, false in a terminal and in Sky.
    /// </summary>
    public static bool Needed
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            uint length = 0;
            try { return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage; }
            catch (EntryPointNotFoundException) { return false; }   // no package model at all
        }
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
