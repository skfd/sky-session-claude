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

    [Fact]
    public void A_test_run_has_no_package_so_launches_stay_ordinary()
    {
        // The test host runs from a terminal or an IDE, neither of them packaged. Were this
        // true here, every terminal Sky opened from a double-click would go through WMI too.
        Assert.False(OutsideJob.Needed);
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
