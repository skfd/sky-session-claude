using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SessionCore;

/// <summary>What became of a repo's check when a session declared it was done.</summary>
public enum CheckOutcome
{
    Passed,
    Failed,

    /// <summary>
    /// The check could not be run at all: no shell, a timeout, a definition that would not
    /// parse. Never counted as a failure — a broken mechanism says nothing about the work.
    /// </summary>
    NotRun,
}

/// <summary>
/// One run of a repo's check, stamped on the declaration it was run for. Record only: it
/// never changes the state that was declared, and nothing reads it as a refusal.
/// </summary>
public sealed record CheckResult
{
    public required CheckOutcome Outcome { get; init; }

    /// <summary>Short HEAD of the tree that was checked; null outside git.</summary>
    public string? Commit { get; init; }

    /// <summary>Tracked files differed from HEAD — what passed is not quite what is committed.</summary>
    public bool Dirty { get; init; }

    public DateTimeOffset At { get; init; }

    public double Seconds { get; init; }

    /// <summary>The exit code and the last lines of output, or why it did not run.</summary>
    public string? Detail { get; init; }

    public static string ToWire(CheckOutcome o) => o switch
    {
        CheckOutcome.Passed => "passed",
        CheckOutcome.Failed => "failed",
        _ => "not-run",
    };

    public static CheckOutcome FromWire(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "passed" => CheckOutcome.Passed,
        "failed" => CheckOutcome.Failed,
        _ => CheckOutcome.NotRun,
    };
}

/// <summary>A repo's own idea of "the work holds": one command, run from the repo root.</summary>
public sealed record CheckDefinition
{
    public required string Command { get; init; }
    public required string Root { get; init; }
    public int TimeoutSeconds { get; init; } = DoneCheck.DefaultTimeoutSeconds;
}

/// <summary>
/// The check a session's "done" is held against — decided 2026-11-09 to run at the
/// declaration and record, not to block (NEXT.md). Opt-in by presence: a repo defines one in
/// <see cref="DefinitionPath"/>, and a repo without one is never checked. Most have none.
///
/// Only <c>needs-read</c> and <c>exhausted</c> are checked. <c>runnable</c> and
/// <c>blocked</c> say work is still owed, and a broken build with work queued is honest.
/// </summary>
public static class DoneCheck
{
    public const string DefinitionPath = ".claude/sky-check.json";
    public const int DefaultTimeoutSeconds = 600;
    private const int TailLines = 15;

    public static bool Applies(Declared state) => state is Declared.NeedsRead or Declared.Exhausted;

    /// <summary>
    /// The definition governing <paramref name="cwd"/>: the nearest one walking up, stopping
    /// at the git root so a repo never inherits a check from a folder it merely sits in.
    /// Null when there is none. Throws on one that exists and will not parse — the caller
    /// records that as not-run rather than pretending the repo has no check.
    /// </summary>
    public static CheckDefinition? Find(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            if (!dir.Exists) return null;

            var file = Path.Combine(dir.FullName, DefinitionPath);
            if (File.Exists(file)) return Parse(File.ReadAllText(file), dir.FullName);

            // .git is a folder in a checkout and a file in a worktree; either marks the root.
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return null;
        }
        return null;
    }

    public static CheckDefinition Parse(string json, string root)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        if (!r.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(c.GetString()))
            throw new FormatException($"{DefinitionPath} needs a \"command\".");

        var timeout = DefaultTimeoutSeconds;
        if (r.TryGetProperty("timeoutSeconds", out var t) && t.TryGetInt32(out var secs) && secs > 0)
            timeout = secs;

        return new CheckDefinition { Command = c.GetString()!, Root = root, TimeoutSeconds = timeout };
    }

    public static CheckResult NotRun(string why, string? root = null) => new()
    {
        Outcome = CheckOutcome.NotRun,
        Commit = root is null ? null : Git(root, "rev-parse --short HEAD"),
        At = DateTimeOffset.Now,
        Detail = why,
    };

    /// <summary>
    /// Run the check and say what happened. Never throws: whatever goes wrong with the
    /// running itself comes back as <see cref="CheckOutcome.NotRun"/>.
    /// </summary>
    public static CheckResult Run(CheckDefinition def)
    {
        var started = DateTimeOffset.Now;
        var commit = Git(def.Root, "rev-parse --short HEAD");
        var dirty = !string.IsNullOrEmpty(Git(def.Root, "status --porcelain --untracked-files=no"));
        var watch = Stopwatch.StartNew();

        CheckResult Done(CheckOutcome outcome, string detail) => new()
        {
            Outcome = outcome,
            Commit = commit,
            Dirty = dirty,
            At = started,
            Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            Detail = detail,
        };

        try
        {
            using var p = Start(def);
            if (p is null) return Done(CheckOutcome.NotRun, "no PowerShell to run the check with");

            var output = new Tail(TailLines);
            p.OutputDataReceived += (_, e) => output.Add(e.Data);
            p.ErrorDataReceived += (_, e) => output.Add(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(def.TimeoutSeconds * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return Done(CheckOutcome.NotRun, $"timed out after {def.TimeoutSeconds}s\n{output}");
            }
            p.WaitForExit(); // flush the async readers

            return p.ExitCode == 0
                ? Done(CheckOutcome.Passed, "exit 0")
                : Done(CheckOutcome.Failed, $"exit {p.ExitCode}\n{output}");
        }
        catch (Exception e)
        {
            return Done(CheckOutcome.NotRun, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static Process? Start(CheckDefinition def)
    {
        foreach (var shell in new[] { "pwsh.exe", "powershell.exe" })
        {
            var psi = new ProcessStartInfo(shell)
            {
                WorkingDirectory = def.Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(def.Command);

            try
            {
                var p = Process.Start(psi);
                p?.StandardInput.Close();
                return p;
            }
            catch (Win32Exception) { /* not installed; try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Whether two folders are the same repository — the same checkout, or a worktree of it.
    /// Git's common dir is what a checkout and its worktrees share and a stranger does not.
    /// </summary>
    public static bool SameRepo(string a, string b)
    {
        var ca = Git(a, "rev-parse --path-format=absolute --git-common-dir");
        var cb = Git(b, "rev-parse --path-format=absolute --git-common-dir");
        return ca is not null && cb is not null
               && string.Equals(Path.GetFullPath(ca), Path.GetFullPath(cb), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One line of git's answer, or null when there is no git or no repo.</summary>
    private static string? Git(string root, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{root}\" {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The last few lines of output, kept as they arrive — a test run can print thousands.</summary>
    private sealed class Tail(int keep)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lines)
            {
                _lines.Enqueue(line.TrimEnd());
                while (_lines.Count > keep) _lines.Dequeue();
            }
        }

        public override string ToString()
        {
            lock (_lines) return string.Join("\n", _lines);
        }
    }
}
