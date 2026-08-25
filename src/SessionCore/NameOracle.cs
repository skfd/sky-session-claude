using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SessionCore;

/// <summary>What an oracle call produced, and what it cost.</summary>
/// <param name="Subject">What the session was about, or null when nothing came back.</param>
/// <param name="SessionId">
/// The transcript the call itself created, reported by <c>--output-format json</c>. This is
/// what makes cleanup targetable: Sky deletes exactly the file its own call produced, never
/// one it inferred from a timestamp or a folder.
/// </param>
public readonly record struct OracleResult(
    string? Subject, string? SessionId, string? Error, TimeSpan Elapsed)
{
    public bool Ok => !string.IsNullOrEmpty(Subject);
}

/// <summary>
/// Asks a small model what a session was about, for the sessions nothing cheaper can name.
///
/// This is the last of the four sources in docs/NAMING.md and the only one that costs
/// anything: about two cents and five seconds a name. It is for the sessions that predate the
/// CLAUDE.md line — the ones that never had the chance to name themselves and carry no
/// <c>aiTitle</c> either. <see cref="NamePolicy.WantsOracle"/> is the gate, and it refuses
/// every case a free source could have covered.
///
/// It blocks, and the caller waits. That is tolerable only while it stays rare, which is why
/// nothing here calls it on a timer: a sweep over a dozen unnamed sessions would be a minute
/// of waiting and a bill nobody asked for. Being asked is the whole permission structure —
/// renaming is free and Sky does it unasked, and this is not free, so it does not.
///
/// The flags are not arbitrary; each was measured (docs/NAMING.md, "The claude -p path"):
/// MCP off is 3.6 of the 8.9 seconds, the prompt goes on stdin because the variadic flags
/// swallow a trailing argument, and the scratch cwd means a call interrupted before cleanup
/// leaks into an obvious junk folder rather than into a real repo.
/// </summary>
public static class NameOracle
{
    /// <summary>
    /// Long enough for a cold start on a slow morning, short enough that a caller waiting on
    /// it does not think the tool has hung.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How much conversation to send. Enough for the model to see what the session grew into
    /// rather than only what it opened with — the drift rule in docs/NAMING.md is the whole
    /// reason this is a model's judgement and not a rule.
    /// </summary>
    private const int MaxPrompts = 40;
    private const int MaxPromptChars = 400;
    private const int MaxTotalChars = 12_000;

    /// <summary>
    /// What <paramref name="info"/> was about, in the model's words.
    ///
    /// Deleting the transcript this creates is the caller's, through
    /// <see cref="CleanUp"/> — kept separate so a caller can see what was made before it goes.
    /// </summary>
    public static async Task<OracleResult> SubjectOfAsync(SessionInfo info, string? scratchDir = null)
    {
        var started = Stopwatch.StartNew();
        var dir = scratchDir ?? ScratchDir();

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, null, $"could not make a scratch folder: {e.Message}", started.Elapsed);
        }

        var prompt = PromptFor(info);

        try
        {
            var (stdout, stderr, exit) = await RunAsync(dir, prompt);

            if (exit != 0)
                return new(null, null, Trim(stderr) is { Length: > 0 } why ? why : $"claude exited {exit}",
                    started.Elapsed);

            var (text, sessionId) = ReadAnswer(stdout);
            return new(Clean(text), sessionId, text is null ? "no answer came back" : null, started.Elapsed);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(null, null, $"could not run claude: {e.Message}", started.Elapsed);
        }
    }

    /// <summary>
    /// Delete the transcript an oracle call left behind, and only that one.
    ///
    /// Reading <c>~/.claude/projects</c> is all this tool has ever done; deleting from it is a
    /// new authority, so it is bounded to the id <c>claude -p</c> itself reported back. An id
    /// that was not reported is never guessed at, and a file that has since been resumed by a
    /// person is still theirs — but it cannot have been, because nothing surfaces these.
    /// </summary>
    public static bool CleanUp(string? sessionId, string? projectsDir = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        // The id has to be a plain uuid before it is turned into a glob, or a caller who
        // could shape it would be choosing which files this deletes.
        if (sessionId.Any(c => !char.IsLetterOrDigit(c) && c != '-')) return false;

        try
        {
            var dir = projectsDir ?? SessionScanner.DefaultProjectsDir();
            if (!Directory.Exists(dir)) return false;

            bool deleted = false;
            foreach (var file in new DirectoryInfo(dir)
                .EnumerateDirectories()
                .SelectMany(d => d.EnumerateFiles($"{sessionId}.jsonl")))
            {
                file.Delete();
                deleted = true;
            }
            return deleted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where a call that dies before cleanup leaves its mess: somewhere obviously junk.</summary>
    public static string ScratchDir() => Path.Combine(
        Path.GetTempPath(), "sky-session-claude-naming");

    // --- the call -----------------------------------------------------------

    private static async Task<(string Out, string Err, int Exit)> RunAsync(string cwd, string prompt)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // -p with no trailing argument: the prompt goes on stdin, because --disallowedTools and
        // its neighbours are variadic and would eat one.
        foreach (var arg in new[]
        {
            "-p",
            "--model", "haiku",
            "--output-format", "json",

            // Naming needs no Figma connector, and booting them is 3.6 of the 8.9 seconds.
            "--strict-mcp-config",
            "--mcp-config", """{"mcpServers":{}}""",

            // Pure text. It matters more once the CLAUDE.md line is installed: an oracle call
            // reads that line too, and a call with no tools cannot act on it.
            "--disallowedTools", "Bash,Read,Write,Edit,Glob,Grep,WebFetch,WebSearch,Task,Agent",
        })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("claude did not start");

        // Reading is started before writing. The prompt can run to twelve kilobytes and the
        // pipes hold far less, so a child that answered before draining its stdin would wedge
        // against a parent that had not started draining its stdout.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return ("", $"gave no answer within {Timeout.TotalSeconds:0}s", -1);
        }

        return (await stdout, await stderr, process.ExitCode);
    }

    // --- what it is asked ---------------------------------------------------

    /// <summary>
    /// The question. It asks for the largest thing the session did rather than the newest,
    /// because a session that ends on a push is not a session about pushing — that is the one
    /// case docs/NAMING.md says needs judgement rather than a rule.
    /// </summary>
    public static string PromptFor(SessionInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Below is a transcript summary of a coding session. Reply with a short title for it:");
        sb.AppendLine("what the session was mostly about, in sentence case, plain words, under 45 characters.");
        sb.AppendLine("Name the largest thing it did, not the last thing or the first — a session that ends on a");
        sb.AppendLine("commit is not a session about committing. Do not name the folder; that is added separately.");
        sb.AppendLine("Reply with the title alone: no quotes, no backticks, no explanation.");
        sb.AppendLine();
        sb.AppendLine($"Folder: {SessionName.RepoOf(info.RealCwd)}");
        sb.AppendLine();

        sb.AppendLine("What the operator asked for, in order:");
        int budget = MaxTotalChars;
        foreach (var line in PromptsIn(info.FilePath))
        {
            var text = SessionName.Tidy(line, MaxPromptChars);
            if (text.Length == 0) continue;
            if (budget - text.Length < 0) break;
            budget -= text.Length;
            sb.AppendLine($"- {text}");
        }

        if (info.Recap is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"How it left off: {SessionName.Tidy(info.Recap, 600)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The operator's own turns, oldest first — the spine of what a session was for. Read
    /// straight off the file rather than through the parser, which keeps only the last one.
    ///
    /// Both places they live. The <c>last-prompt</c> pointer is the obvious one and is often
    /// null; the <c>user</c> records are where most transcripts actually keep the asks, and
    /// reading only the pointer meant paying for a call that had been shown nearly nothing.
    /// </summary>
    private static IEnumerable<string> PromptsIn(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) yield break;

        var found = new List<string>();
        IEnumerable<string> lines;
        try { lines = File.ReadLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var line in lines)
        {
            if (!line.Contains("\"lastPrompt\"", StringComparison.Ordinal)
                && !line.Contains("\"user\"", StringComparison.Ordinal)) continue;

            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                text = AskIn(doc.RootElement);
            }
            catch (JsonException) { continue; }

            // A resume rewrites the same last-prompt record, and the pointer repeats whatever
            // the last user record already said; sending an ask twice weights it as two asks.
            if (text is { Length: > 0 } && !found.Contains(text))
                found.Add(text);
        }

        foreach (var text in found.Count > MaxPrompts ? found.TakeLast(MaxPrompts) : found)
            yield return text;
    }

    /// <summary>
    /// The operator's words in one record, or null if it holds none. Tool results and the
    /// harness's own injections (<c>/clear</c>, system reminders, local-command wrappers) are
    /// not asks, and reading them as asks would have the model name the session after the
    /// scaffolding rather than the work.
    /// </summary>
    private static string? AskIn(JsonElement o)
    {
        if (o.TryGetProperty("lastPrompt", out var lp) && lp.ValueKind == JsonValueKind.String)
            return Asked(lp.GetString());

        if (o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            && t.GetString() == "user"
            && o.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return Asked(content.GetString());

            if (content.ValueKind == JsonValueKind.Array)
                foreach (var item in content.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var it) && it.GetString() == "text"
                        && item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                        return Asked(tx.GetString());
        }

        return null;
    }

    /// <summary>Null for anything that is scaffolding rather than someone asking for something.</summary>
    private static string? Asked(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)
            ? null
            : text;
    }

    // --- what comes back ----------------------------------------------------

    /// <summary>
    /// The answer and the id of the transcript the call created. <c>--output-format json</c>
    /// is always on for the second of those: it is what makes deleting the file safe.
    /// </summary>
    public static (string? Text, string? SessionId) ReadAnswer(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            var id = root.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;

            return (text, id);
        }
        catch (JsonException)
        {
            // Not the shape we asked for. The text may still be usable; the transcript is then
            // not ours to delete, because nothing told us which one it is.
            return (Trim(stdout) is { Length: > 0 } text ? text : null, null);
        }
    }

    /// <summary>
    /// The answer as a subject. Haiku wraps a title in backticks given half a chance, and
    /// quotes it given the other half.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim().Trim('`', '"', '\'', '*').Trim();

        // A model that explained itself anyway said the title first; the rest is prose.
        int stop = trimmed.IndexOf('\n');
        if (stop > 0) trimmed = trimmed[..stop].Trim();

        var subject = SessionName.SentenceCase(SessionName.Tidy(trimmed.TrimEnd('.')));
        return subject.Length > 0 ? subject : null;
    }

    private static string Trim(string? text) => text?.Trim() ?? "";
}
