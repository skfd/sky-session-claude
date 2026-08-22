using System.Diagnostics;
using SessionCore;

namespace SessionCli;

/// <summary>
/// The verbs. Everything here is the same machinery the app drives from a keystroke —
/// <see cref="DispositionStore"/>, <see cref="SessionForker"/>, <see cref="RestartPolicy"/>,
/// <see cref="SessionRestarter"/> — with a command line in front of it instead of a window.
///
/// Two rules hold across all of them:
/// <list type="bullet">
/// <item>Say what happened, in JSON, every time. A verb that changed nothing says so rather
///       than exiting quietly, because the caller is a script and cannot look at a card.</item>
/// <item>Never act on the session the command is running inside. An agent restarting its own
///       session kills itself mid-sentence; that needs <c>--force</c> and a good reason.</item>
/// </list>
/// </summary>
internal static class Commands
{
    // --- reading ------------------------------------------------------------

    public static int List(Args args)
    {
        args.RejectUnknown("json", "top", "newest-per-project", "context-window",
            "status", "project", "search", "disposition", "unfinished", "live", "stale", "limit");

        var scanner = RequireScanner();
        var options = new ScanOptions
        {
            All = !args.Has("newest-per-project"),
            // The app defaults to every session for a reason: a cap can hide an old
            // unfinished session just past the cut, which is the one worth finding.
            Top = args.Int("top", int.MaxValue),
            ContextWindow = args.Int("context-window", SessionFileParser.DefaultContextWindow),
        };

        var store = new DispositionStore();
        var live = LiveSessions.Scan();
        var installed = ClaudeInstall.InstalledVersion;

        var rows = scanner.Scan(options)
            .Select(info => SessionDto.From(
                info,
                store.Get(info.SessionId),
                LiveFor(live, info.SessionId, info.Status, installed)))
            .Where(row => Matches(row, args))
            .ToList();

        if (args.Int("limit", 0) is > 0 and var limit) rows = rows.Take(limit).ToList();

        var path = args.Has("json") ? args.Require("json") : null;
        Cli.Emit(new ExportDto
        {
            GeneratedAt = DateTimeOffset.Now.ToString("o"),
            Count = rows.Count,
            Sessions = rows,
            Warning = store.LoadWarning,
        }, path);

        if (path is not null)
            Console.Error.WriteLine($"Wrote {rows.Count} session(s) to {path}");
        return 0;
    }

    private static bool Matches(SessionDto row, Args args)
    {
        if (args.Value("status") is { } status
            && !row.Status.Equals(status, StringComparison.OrdinalIgnoreCase)) return false;

        if (args.Value("project") is { } project
            && !row.Project.Contains(project, StringComparison.OrdinalIgnoreCase)) return false;

        if (args.Value("disposition") is { } disposition
            && !row.Disposition.Equals(disposition, StringComparison.OrdinalIgnoreCase)) return false;

        if (args.Has("unfinished") && row.Settled) return false;
        if (args.Has("live") && row.Live is null) return false;
        if (args.Has("stale") && row.Live is not { Stale: true }) return false;

        if (args.Value("search") is { } search)
        {
            bool hit =
                row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Project.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.LastPrompt.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Recap.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        return true;
    }

    public static int Show(Args args)
    {
        args.RejectUnknown("context-window");

        var scanner = RequireScanner();
        var file = Resolve(scanner, OneId(args, "show"));
        var info = scanner.BuildRow(file, args.Int("context-window", SessionFileParser.DefaultContextWindow));

        var live = LiveSessions.Scan();
        var forkPoints = new List<ForkPointDto>();
        try
        {
            forkPoints = SessionForker.ListForkPoints(file.FullName).Select(ForkPointDto.From).ToList();
        }
        catch (IOException)
        {
            // A session being written to right now is still worth showing; it just cannot
            // be forked at this instant.
        }

        Cli.Emit(new ShowDto
        {
            Session = SessionDto.From(
                info,
                new DispositionStore().Get(info.SessionId),
                LiveFor(live, info.SessionId, info.Status, ClaudeInstall.InstalledVersion)),
            FilePath = file.FullName,
            ForkPoints = forkPoints,
        });
        return 0;
    }

    /// <summary>
    /// What is running right now, straight from the registry — no session file is opened,
    /// so this answers in milliseconds however many sessions are on disk.
    /// </summary>
    public static int Live(Args args)
    {
        args.RejectUnknown();

        var installed = ClaudeInstall.InstalledVersion;
        var running = LiveSessions.All()
            .OrderBy(s => s.Pid)
            .Select(s => new
            {
                s.SessionId,
                s.Pid,
                s.Name,
                s.Cwd,
                s.Version,
                State = s.Status,
                s.RemoteControl,
                Stale = ClaudeInstall.IsStale(s.Version, installed),
                // No session file was read, so the classifier's tail is unknown here and
                // the verdict is the registry's half of the question only.
                Restart = RestartPolicy.Judge(s, null, DateTime.Now).Reason,
            })
            .ToList();

        Cli.Emit(new { Installed = installed, Count = running.Count, Sessions = running });
        return 0;
    }

    // --- marking ------------------------------------------------------------

    public static int Mark(Args args, Disposition disposition)
    {
        args.RejectUnknown("dry-run");

        var ids = args.Positional;
        if (ids.Count == 0)
            throw new UsageException($"'{args.Verb}' needs at least one session id.");

        var scanner = RequireScanner();
        var files = ids.Select(id => Resolve(scanner, id)).ToList();

        var store = new DispositionStore();
        var items = files.Select(file =>
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            var before = store.Get(id);
            return new ActionItem
            {
                SessionId = id,
                // Worth the one parse: a confirmation that names the session is how you
                // notice you marked the wrong one.
                Name = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow).Name,
                Ok = true,
                Message = before == disposition
                    ? $"already {DispositionStore.ToWire(disposition)}"
                    : $"{DispositionStore.ToWire(before)} -> {DispositionStore.ToWire(disposition)}",
            };
        }).ToList();

        if (!args.Has("dry-run"))
            store.SetMany(items.Select(i => i.SessionId), disposition);

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = args.Verb,
            Message = args.Has("dry-run")
                ? $"Would mark {items.Count} session(s) {DispositionStore.ToWire(disposition)}."
                : $"Marked {items.Count} session(s) {DispositionStore.ToWire(disposition)}."
                  + (store.LoadWarning is { Length: > 0 } w ? $"  Warning: {w}." : ""),
            Items = items,
        });
    }

    // --- forking ------------------------------------------------------------

    public static int Fork(Args args)
    {
        args.RejectUnknown("at-prompt", "tip", "resume", "dry-run");

        var scanner = RequireScanner();
        var file = Resolve(scanner, OneId(args, "fork"));
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);

        bool tip = args.Has("tip");
        bool atPrompt = args.Has("at-prompt");
        if (tip == atPrompt)
            throw new UsageException(
                "'fork' needs either --at-prompt <n> (a file fork, no terminal) or --tip "
                + "(the official fork, which opens a terminal). Run `show` to see the prompts.");

        // The tip fork is the CLI's own `--fork-session`, which only exists as a running
        // session — there is no file to write, so this one has to open a terminal.
        if (tip)
        {
            var info = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow);
            var command = info.Command + " --fork-session";
            if (!args.Has("dry-run")) StartTerminal(command);

            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "fork",
                Message = args.Has("dry-run")
                    ? $"Would run: {command}"
                    : $"Forking \"{info.Name ?? sessionId}\" at the tip in a new terminal.",
            });
        }

        int ordinal = args.Int("at-prompt", 0);
        var points = SessionForker.ListForkPoints(file.FullName);
        var point = points.FirstOrDefault(p => p.Ordinal == ordinal)
            ?? throw new UsageException(
                $"Prompt {ordinal} is not a fork point in {sessionId}. "
                + $"Forkable prompts: {(points.Count == 0 ? "none" : string.Join(", ", points.Select(p => p.Ordinal)))}.");

        if (args.Has("dry-run"))
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "fork",
                Message = $"Would fork {sessionId} from before prompt {ordinal}: “{point.Prompt}”",
            });

        // The original file is never touched, so the worst a format drift can do is produce
        // a fork that fails to resume and gets deleted.
        var newId = SessionForker.ForkFrom(file.FullName, point.LeafUuid);
        var cwd = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow).Cwd;
        var resume = cwd is { Length: > 0 } ? $"cd \"{cwd}\"; claude --resume {newId}" : $"claude --resume {newId}";
        if (args.Has("resume")) StartTerminal(resume);

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "fork",
            Message = $"Forked {sessionId} from before prompt {ordinal}. Resume it with: {resume}",
            Items =
            [
                new ActionItem
                {
                    SessionId = sessionId,
                    Ok = true,
                    Message = $"forked from before prompt {ordinal}",
                    NewSessionId = newId,
                },
            ],
        });
    }

    // --- restarting ---------------------------------------------------------

    public static int Restart(Args args)
    {
        args.RejectUnknown("stale", "yes", "force", "dry-run");

        var scanner = RequireScanner();
        var installed = ClaudeInstall.InstalledVersion;
        var live = LiveSessions.Scan();
        bool dry = args.Has("dry-run");

        List<(LiveSession Live, SessionStatus? Tail, string Name)> targets;
        List<ActionItem> skipped = new();

        if (args.Has("stale"))
        {
            if (args.Positional.Count > 0)
                throw new UsageException("'restart --stale' takes no session ids.");

            var store = new DispositionStore();
            var infos = scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue })
                .ToDictionary(i => i.SessionId, StringComparer.OrdinalIgnoreCase);

            targets = new List<(LiveSession, SessionStatus?, string)>();
            foreach (var session in live.Values.SelectMany(v => v))
            {
                var info = infos.GetValueOrDefault(session.SessionId);
                var name = Titled(info?.Name) ?? session.Name ?? session.SessionId;

                if (!ClaudeInstall.IsStale(session.Version, installed)) continue;
                if (store.Get(session.SessionId) == Disposition.Abandoned) continue;
                if (IsSelf(session.SessionId) && !args.Has("force"))
                {
                    skipped.Add(Skip(session.SessionId, name, "it is the session this command is running in"));
                    continue;
                }

                var verdict = RestartPolicy.Judge(session, info?.Status, DateTime.Now);
                // A sweep is the set where nothing can be lost, not the set that looks
                // quiet — anything merely plausible is reported rather than taken.
                if (!verdict.CanSweep) skipped.Add(Skip(session.SessionId, name, verdict.Reason));
                else targets.Add((session, info?.Status, name));
            }

            // The sweep drives terminals nobody is looking at, so it states its plan and
            // waits to be told twice. A single named session does not need that.
            if (!args.Has("yes")) dry = true;
        }
        else
        {
            if (args.Positional.Count == 0)
                throw new UsageException("'restart' needs a session id, or --stale.");

            targets = new List<(LiveSession, SessionStatus?, string)>();
            foreach (var id in args.Positional)
            {
                var file = Resolve(scanner, id);
                var info = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow);
                var name = info.Name ?? info.SessionId;

                if (!live.TryGetValue(info.SessionId, out var running) || running.Count == 0)
                {
                    skipped.Add(Skip(info.SessionId, name, "it is not open in a terminal"));
                    continue;
                }

                if (IsSelf(info.SessionId) && !args.Has("force"))
                {
                    skipped.Add(Skip(info.SessionId, name,
                        "it is the session this command is running in — pass --force if you mean it"));
                    continue;
                }

                var verdict = RestartPolicy.Judge(running[0], info.Status, DateTime.Now);
                if (verdict.Safety == RestartSafety.Unsafe)
                {
                    skipped.Add(Skip(info.SessionId, name, verdict.Reason));
                    continue;
                }

                targets.Add((running[0], info.Status, name));
            }
        }

        var items = new List<ActionItem>();
        int done = 0;

        foreach (var (session, tail, name) in targets)
        {
            if (dry)
            {
                items.Add(new ActionItem
                {
                    SessionId = session.SessionId,
                    Name = name,
                    Ok = true,
                    Message = $"would restart: {RestartPolicy.RelaunchLine(session)}",
                });
                continue;
            }

            var result = SessionRestarter.RestartAsync(session).GetAwaiter().GetResult();
            if (result.Ok) done++;
            items.Add(new ActionItem
            {
                SessionId = session.SessionId,
                Name = name,
                Ok = result.Ok,
                Message = result.Message,
            });
        }

        items.AddRange(skipped);

        var message = dry
            ? $"Would restart {targets.Count} session(s)"
                + (skipped.Count > 0 ? $"; skipping {skipped.Count}" : "")
                + (args.Has("stale") && !args.Has("yes") ? ". Re-run with --yes to do it." : ".")
            : $"Restarted {done} of {targets.Count}"
                + (skipped.Count > 0 ? $"; skipped {skipped.Count}" : "") + ".";

        // Naming a session and getting nothing is a failure the caller should see in the
        // exit code. A sweep skipping some is not — reporting what it left is the job.
        bool ok = dry
            || (args.Has("stale") ? done == targets.Count
                                  : done == targets.Count && skipped.Count == 0);

        return Cli.EmitResult(new ActionResult
        {
            Ok = ok,
            Action = "restart",
            Message = message,
            Items = items,
        });
    }

    /// <summary>
    /// A real title, or null. The scanner fills an untitled session in with "(untitled)",
    /// which is a placeholder rather than a name — the running CLI usually knows a better
    /// one ("sky-session-claude-87"), and that is worth preferring over a shrug.
    /// </summary>
    private static string? Titled(string? name) =>
        string.IsNullOrEmpty(name) || name == "(untitled)" ? null : name;

    private static ActionItem Skip(string id, string name, string why) => new()
    {
        SessionId = id,
        Name = name,
        Ok = false,
        Message = $"skipped — {why}",
    };

    // --- resuming -----------------------------------------------------------

    public static int Resume(Args args)
    {
        args.RejectUnknown("dry-run");

        var scanner = RequireScanner();
        var file = Resolve(scanner, OneId(args, "resume"));
        var info = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow);

        if (string.IsNullOrEmpty(info.Command))
            throw new UsageException($"{info.SessionId} has no resumable command (no recorded cwd).");

        // Already up: a second `claude --resume` against the same session would be a
        // duplicate, and this side of the app cannot raise the window it is already in.
        if (LiveSessions.Find(info.SessionId) is { } running)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "resume",
                Message = $"\"{info.Name ?? info.SessionId}\" is already open in a terminal (pid {running.Pid}).",
            });

        if (!args.Has("dry-run")) StartTerminal(info.Command);

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "resume",
            Message = args.Has("dry-run")
                ? $"Would run: {info.Command}"
                : $"Opened a terminal running: {info.Command}",
        });
    }

    // --- shared -------------------------------------------------------------

    private static SessionScanner RequireScanner()
    {
        var scanner = new SessionScanner();
        if (!scanner.ProjectsDirExists)
            throw new UsageException($"No Claude Code projects folder found at: {scanner.ProjectsDir}");
        return scanner;
    }

    private static string OneId(Args args, string verb) =>
        args.Positional.Count == 1
            ? args.Positional[0]
            : throw new UsageException($"'{verb}' takes exactly one session id.");

    /// <summary>
    /// The session file for an id or any unique prefix of one. Ambiguity is an error rather
    /// than a guess: the verbs behind this restart terminals and write files.
    /// </summary>
    private static FileInfo Resolve(SessionScanner scanner, string idOrPrefix)
    {
        var matches = scanner.FindByPrefix(idOrPrefix);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new UsageException($"No session matches '{idOrPrefix}'."),
            _ => throw new UsageException(
                $"'{idOrPrefix}' matches {matches.Count} sessions: "
                + string.Join(", ", matches.Take(5).Select(m => Path.GetFileNameWithoutExtension(m.Name)))
                + (matches.Count > 5 ? ", ..." : "") + ". Use a longer prefix."),
        };
    }

    private static LiveDto? LiveFor(
        Dictionary<string, List<LiveSession>> live, string sessionId, SessionStatus tail, string? installed) =>
        live.TryGetValue(sessionId, out var running) && running.Count > 0
            ? LiveDto.From(running[0], tail, installed)
            : null;

    /// <summary>
    /// The session this command is running inside, if any. Claude Code exports it to every
    /// process it launches, which is exactly how an agent ends up running this binary.
    /// </summary>
    private static bool IsSelf(string sessionId) =>
        Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } self
        && string.Equals(self, sessionId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open a terminal and run a command in it.
    ///
    /// If this process was itself launched from a Claude session it inherited that
    /// session's markers; passing them on makes the resumed session think it is a nested
    /// child and skip saving its transcript. UseShellExecute must be false to edit the
    /// child environment at all.
    /// </summary>
    private static void StartTerminal(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoExit", "-Command", command },
            UseShellExecute = false,
        };
        psi.Environment.Remove("CLAUDE_CODE_CHILD_SESSION");
        psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        Process.Start(psi);
    }
}
