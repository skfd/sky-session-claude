using System.Diagnostics;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// A launch that must outlive the desktop app has to be the WMI host's child rather than
/// ours: that is what leaves it out of the app's job. These start windowless processes only
/// — nothing here opens a terminal.
/// </summary>
public class OutsideJobTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private const string App = @"C:\Program Files\WindowsApps\Claude_4.23118.1.0_x64__pzs8sxrjxfjjc\app\claude.exe";
    private const string Cli = @"C:\Users\kk\.local\bin\claude.exe";
    private static readonly DateTime T0 = new(2026, 11, 9, 17, 0, 0);

    private static OutsideJob.ProcessEntry E(int parent, string? path, int minute) =>
        new(parent, path, T0.AddMinutes(minute));

    [Fact]
    public void A_shell_under_a_session_in_the_desktop_app_is_held_by_it()
    {
        // SessionCli <- pwsh <- claude CLI <- the app, the line an agent's `new` runs in.
        var table = new Dictionary<int, OutsideJob.ProcessEntry>
        {
            [10] = E(4, @"C:\Windows\explorer.exe", 0),
            [20] = E(10, App, 1),
            [30] = E(20, Cli, 2),
            [40] = E(30, @"C:\Program Files\PowerShell\7\pwsh.exe", 3),
            [50] = E(40, @"C:\Tools\SessionCli.exe", 4),
        };
        Assert.Equal(App, OutsideJob.ClaudeAppAncestor(table, 50));
    }

    [Fact]
    public void A_session_in_a_terminal_is_not()
    {
        // The CLI is called claude.exe too; only the packaged app's copy holds its children.
        var table = new Dictionary<int, OutsideJob.ProcessEntry>
        {
            [10] = E(4, @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24_x64__8wekyb3d8bbwe\WindowsTerminal.exe", 0),
            [20] = E(10, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 1),
            [30] = E(20, Cli, 2),
            [40] = E(30, @"C:\Tools\SessionCli.exe", 3),
        };
        Assert.Null(OutsideJob.ClaudeAppAncestor(table, 40));
    }

    [Fact]
    public void A_reused_parent_id_is_a_stranger()
    {
        // The app that started 30 quit; its id went to a newer app process. Not our parent.
        var table = new Dictionary<int, OutsideJob.ProcessEntry>
        {
            [20] = E(1, App, 9),
            [30] = E(20, @"C:\Tools\SessionCli.exe", 5),
        };
        Assert.Null(OutsideJob.ClaudeAppAncestor(table, 30));
    }

    [Fact]
    public void A_loop_of_parent_ids_ends_the_walk()
    {
        var table = new Dictionary<int, OutsideJob.ProcessEntry>
        {
            [20] = E(30, @"C:\a.exe", 0),
            [30] = E(20, @"C:\b.exe", 0),
        };
        Assert.Null(OutsideJob.ClaudeAppAncestor(table, 30));
    }

    [Fact]
    public void The_started_process_is_the_WMI_hosts_child_not_ours()
    {
        var pid = OutsideJob.Start($"\"{Cmd}\" /c ping -n 6 127.0.0.1 >nul", null, hidden: true);
        Assert.NotNull(pid);
        try
        {
            var parent = OutsideJob.ParentOf(pid.Value);
            Assert.NotNull(parent);
            Assert.NotEqual(Environment.ProcessId, parent);
            using var host = Process.GetProcessById(parent.Value);
            Assert.Equal("WmiPrvSE", host.ProcessName, ignoreCase: true);
        }
        finally
        {
            try { using var p = Process.GetProcessById(pid.Value); p.Kill(); } catch (ArgumentException) { }
        }
    }

    [Fact]
    public void It_starts_in_the_folder_it_is_given()
    {
        var folder = Directory.CreateTempSubdirectory("outside-job-").FullName;
        var output = Path.Combine(folder, "cwd.txt");
        try
        {
            var pid = OutsideJob.Start($"\"{Cmd}\" /c cd > cwd.txt", folder, hidden: true);
            Assert.NotNull(pid);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            string? text = null;
            while (DateTime.UtcNow < deadline)
            {
                try { text = File.ReadAllText(output).Trim(); if (text.Length > 0) break; }
                catch (IOException) { }
                Thread.Sleep(100);
            }

            Assert.Equal(folder.TrimEnd('\\'), text, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
