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
        // A fork is named after the prompt it branched at, because that is what it is *for*.
        // Left to inherit the parent's title, every branch of one session reads identically --
        // and a fork you cannot pick out of the list is a fork you will not go back to.
        var forkName = SessionName.Tidy($"fork: {point.Prompt}");

        var newId = SessionForker.ForkFrom(file.FullName, point.LeafUuid, forkName);

        // Recorded in the same operation as the write, like every other name Sky puts down.
        // Chosen, because branching here was the operator's decision and the name is only
        // saying which decision it was -- nothing Sky reads later can improve on that.
        new NameStore().Record(newId, forkName, NameOrigin.Chosen);

        var cwd = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow).Cwd;
        var named = $"claude --resume {newId} --name {SessionName.Quote(forkName)}";
        var resume = cwd is { Length: > 0 } ? $"cd \"{cwd}\"; {named}" : named;
        if (args.Has("resume")) StartTerminal(resume);

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "fork",
            Message = $"Forked {sessionId} from before prompt {ordinal} as \"{forkName}\". Resume it with: {resume}",
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

        // What a session comes back under is the policy's call, so each target carries what
        // the policy needs rather than a title this verb would have to interpret itself.
        List<(LiveSession Live, SessionStatus? Tail, string Name, NameInputs Inputs)> targets;
        List<ActionItem> skipped = new();

        var names = new NameStore();
        var liveNames = SessionNaming.LiveNamesOf(live);

        if (args.Has("stale"))
        {
            if (args.Positional.Count > 0)
                throw new UsageException("'restart --stale' takes no session ids.");

            var store = new DispositionStore();
            var infos = scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue })
                .ToDictionary(i => i.SessionId, StringComparer.OrdinalIgnoreCase);

            targets = new List<(LiveSession, SessionStatus?, string, NameInputs)>();
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
                else targets.Add((session, info?.Status, name,
                    info is not null
                        ? SessionNaming.InputsFor(info, session, liveNames)
                        : SessionNaming.InputsFor(session, liveNames)));
            }

            // The sweep drives terminals nobody is looking at, so it states its plan and
            // waits to be told twice. A single named session does not need that.
            if (!args.Has("yes")) dry = true;
        }
        else
        {
            if (args.Positional.Count == 0)
                throw new UsageException("'restart' needs a session id, or --stale.");

            targets = new List<(LiveSession, SessionStatus?, string, NameInputs)>();
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
                if (verdict.Safety == SweepSafety.Unsafe)
                {
                    skipped.Add(Skip(info.SessionId, name, verdict.Reason));
                    continue;
                }

                targets.Add((running[0], info.Status, name,
                    SessionNaming.InputsFor(info, running[0], liveNames)));
            }
        }

        var items = new List<ActionItem>();
        int done = 0;

        foreach (var (session, tail, name, inputs) in targets)
        {
            if (dry)
            {
                // Planned, not recorded: a dry run changes nothing, names.json included.
                var planned = SessionNaming.PlanLaunch(inputs, names).Name;
                items.Add(new ActionItem
                {
                    SessionId = session.SessionId,
                    Name = name,
                    Ok = true,
                    Message = $"would restart: {RestartPolicy.RelaunchLine(session, planned)}",
                });
                continue;
            }

            var launchName = SessionNaming.NameForLaunch(inputs, names);
            var result = SessionRestarter.RestartAsync(session, launchName).GetAwaiter().GetResult();
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

    // --- closing ------------------------------------------------------------

    /// <summary>
    /// Quit live sessions and take their terminals with them — the end-of-day cleanup.
    ///
    /// Two forms, and the difference between them is whose judgment is being used.
    /// <c>close &lt;id&gt;</c> is you pointing at a session, so it proceeds on anything the
    /// policy merely wants to ask about. <c>close --finished</c> drives terminals nobody is
    /// looking at, so it takes only what <see cref="ClosePolicy"/> can prove is over, states
    /// its plan, and waits to be told twice.
    /// </summary>
    public static int Close(Args args)
    {
        args.RejectUnknown("finished", "yes", "force", "dry-run", "keep-terminal");

        var live = LiveSessions.Scan();
        var store = new DispositionStore();
        bool dry = args.Has("dry-run");
        bool keepTerminal = args.Has("keep-terminal");

        var targets = new List<(LiveSession Live, string Name)>();
        var skipped = new List<ActionItem>();

        if (args.Has("finished"))
        {
            if (args.Positional.Count > 0)
                throw new UsageException("'close --finished' takes no session ids.");

            var scanner = RequireScanner();
            var infos = scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue })
                .ToDictionary(i => i.SessionId, StringComparer.OrdinalIgnoreCase);

            foreach (var session in live.Values.SelectMany(v => v))
            {
                // Not a candidate rather than a refusal: there is no console behind the
                // desktop app or the SDK, so naming each one would bury the real report.
                if (!session.InTerminal) continue;

                var info = infos.GetValueOrDefault(session.SessionId);
                var name = Titled(info?.Name) ?? session.Name ?? session.SessionId;

                if (IsSelf(session.SessionId) && !args.Has("force"))
                {
                    skipped.Add(Skip(session.SessionId, name, "it is the session this command is running in"));
                    continue;
                }

                var verdict = ClosePolicy.Judge(
                    session, info?.Status, store.Get(session.SessionId), DateTime.Now, scanned: true);
                if (verdict.CanSweep) targets.Add((session, name));
                else skipped.Add(Skip(session.SessionId, name, verdict.Reason));
            }

            if (!args.Has("yes")) dry = true;
        }
        else
        {
            if (args.Positional.Count == 0)
                throw new UsageException("'close' needs a session id, or --finished.");

            var scanner = RequireScanner();
            foreach (var id in args.Positional)
            {
                // A session with no file is one nobody has typed into yet, and closing it is
                // the whole point — so the id is resolved against what is running, falling
                // back to the scanner only for the ids that got as far as disk.
                var session = ResolveLive(scanner, live, id);
                var info = SessionFileExists(scanner, session.SessionId)
                    ? scanner.BuildRow(Resolve(scanner, session.SessionId), SessionFileParser.DefaultContextWindow)
                    : null;
                var name = Titled(info?.Name) ?? session.Name ?? session.SessionId;

                if (IsSelf(session.SessionId) && !args.Has("force"))
                {
                    skipped.Add(Skip(session.SessionId, name,
                        "it is the session this command is running in — pass --force if you mean it"));
                    continue;
                }

                var verdict = ClosePolicy.Judge(
                    session, info?.Status, store.Get(session.SessionId), DateTime.Now, scanned: true);
                if (verdict.Safety == SweepSafety.Unsafe)
                {
                    skipped.Add(Skip(session.SessionId, name, verdict.Reason));
                    continue;
                }

                targets.Add((session, name));
            }
        }

        var items = new List<ActionItem>();
        int done = 0;

        foreach (var (session, name) in targets)
        {
            if (dry)
            {
                items.Add(new ActionItem
                {
                    SessionId = session.SessionId,
                    Name = name,
                    Ok = true,
                    Message = $"would close: pid {session.Pid}"
                        + (session.Cwd is { Length: > 0 } cwd ? $" in {cwd}" : "")
                        + (keepTerminal ? "" : ", and its terminal"),
                });
                continue;
            }

            var result = SessionCloser.CloseAsync(session, keepTerminal).GetAwaiter().GetResult();
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
            ? $"Would close {targets.Count} session(s)"
                + (skipped.Count > 0 ? $"; leaving {skipped.Count}" : "")
                + (args.Has("finished") && !args.Has("yes") ? ". Re-run with --yes to do it." : ".")
            : $"Closed {done} of {targets.Count}"
                + (skipped.Count > 0 ? $"; left {skipped.Count}" : "") + ".";

        // A sweep that leaves some behind is doing its job; a session you named and did not
        // get is a failure the caller should see in the exit code.
        bool ok = dry
            || (args.Has("finished") ? done == targets.Count
                                     : done == targets.Count && skipped.Count == 0);

        return Cli.EmitResult(new ActionResult
        {
            Ok = ok,
            Action = "close",
            Message = message,
            Items = items,
        });
    }

    /// <summary>
    /// A real title, or null — <see cref="SessionInfo.Title"/> for a name that did not come
    /// from a scanned <see cref="SessionInfo"/>. The placeholder is a shrug rather than a
    /// name, and the running CLI usually knows a better one ("sky-session-claude-87").
    /// </summary>
    private static string? Titled(string? name) =>
        string.IsNullOrEmpty(name) || name == SessionInfo.Untitled ? null : name;

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

        // The name comes from the policy, not from this verb. A launch that composed its own
        // would be a second decider, blind to which names are Sky's, and every resume would
        // write the last placeholder back into the transcript.
        var store = new NameStore();
        var inputs = SessionNaming.InputsFor(info, live: null, SessionNaming.LiveNamesOf(LiveSessions.Scan()));

        // A dry run promises to change nothing, and names.json is something.
        var name = args.Has("dry-run")
            ? SessionNaming.PlanLaunch(inputs, store).Name!
            : SessionNaming.NameForLaunch(inputs, store);

        var command = info.CommandNamed(name);
        if (!args.Has("dry-run")) StartTerminal(command);

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "resume",
            Message = args.Has("dry-run")
                ? $"Would run: {command}"
                : $"Opened a terminal running: {command}",
        });
    }

    // --- renaming -----------------------------------------------------------

    /// <summary>
    /// Give a session a better name, in place.
    ///
    /// This is the one verb that acts on the session it is running inside without being
    /// argued with. Every other refuses -- restarting yourself kills you mid-sentence -- but a
    /// rename touches only the name, so `--self` is the ordinary case rather than the
    /// dangerous one, and it is what the CLAUDE.md line calls when a session's subject
    /// genuinely changes.
    ///
    /// With no name given, the policy decides. That is the same decision the app's background
    /// pass makes, so `rename <id>` and waiting for the app to notice produce the same answer.
    /// </summary>
    public static int Rename(Args args)
    {
        args.RejectUnknown("self", "dry-run", "ask");

        var store = new NameStore();
        var live = LiveSessions.Scan();
        var liveNames = SessionNaming.LiveNamesOf(live);
        bool dry = args.Has("dry-run");

        var (target, given) = Target(args);

        // A prefix has to become a whole id before anything is looked up by it. Doing this
        // the other way round finds the transcript and then misses the live entry, and the
        // verb reports a running session as closed.
        //
        // Ambiguity is an error rather than a guess, as it is everywhere else here: this verb
        // writes into a transcript, and the newest of several matches is not an answer.
        var scanner = new SessionScanner();
        var info = scanner.ProjectsDirExists && scanner.FindByPrefix(target) is { Count: > 0 } matches
            ? scanner.BuildRow(One(matches, target), SessionFileParser.DefaultContextWindow)
            : null;

        var sessionId = info?.SessionId ?? ResolveLive(target)?.SessionId
            ?? throw new UsageException($"No session matches '{target}'.");

        var running = live.TryGetValue(sessionId, out var found) && found.Count > 0 ? found[0] : null;

        var inputs = info is not null
            ? SessionNaming.InputsFor(info, running, liveNames)
            : SessionNaming.InputsFor(running!, liveNames);

        if (args.Has("ask") && info is null)
            throw new UsageException(
                $"{sessionId} has no transcript to read yet, so there is nothing to ask about.");

        // Paying a model to read the session is the one thing here that costs anything, so it
        // happens because it was asked for. Renaming is free and Sky does it unasked; this is
        // not free, so it does not -- and `--ask` on a session a free source could have named
        // is refused rather than quietly spent.
        if (args.Has("ask") && given is null)
        {
            if (!NamePolicy.WantsOracle(inputs, store))
                return Cli.EmitResult(new ActionResult
                {
                    Ok = true,
                    Action = "rename",
                    Message = $"{sessionId} does not need asking: {NamePolicy.Decide(inputs, store).Why}.",
                });

            var answer = NameOracle.SubjectOfAsync(info!).GetAwaiter().GetResult();

            // Cleaned up here, not at the end. A call that failed still made a transcript --
            // often *because* it got far enough to make one -- and the return below would have
            // walked past the tidying, leaving the wreckage of every failure in `list`.
            NameOracle.CleanUp(answer.SessionId);

            if (!answer.Ok)
                return Cli.EmitResult(new ActionResult
                {
                    Ok = false,
                    Action = "rename",
                    Message = $"Could not read {sessionId} -- {answer.Error}.",
                });

            inputs = inputs with { Subject = answer.Subject };
        }

        // A name typed by hand is the operator's, whoever typed it -- including a session
        // naming itself, which is speaking for the conversation rather than for Sky.
        var (name, origin) = given is { Length: > 0 }
            ? (SessionName.Tidy(given), args.Has("self") ? NameOrigin.SelfNamed : NameOrigin.Chosen)
            : Decided(inputs, store);

        if (name.Length == 0)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "rename",
                Message = $"{sessionId} keeps the name it has: {NamePolicy.Decide(inputs, store).Why}.",
            });

        if (dry)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "rename",
                Message = $"Would rename {sessionId} to \"{name}\".",
            });

        // Only a running session has a pipe to be spoken to. A closed one is named on the way
        // back up instead, by `resume` and `restart`, which ask this same policy -- so there
        // is nothing to write here, and no reason to forge a record into someone's transcript.
        //
        // Which also means a name typed here would go nowhere: `resume` asks the policy, and
        // the policy has never heard of it. Saying it will come back under that name would be
        // a promise nothing keeps, so say what it will actually be called instead.
        if (running is null)
        {
            var next = SessionNaming.PlanLaunch(inputs, store).Name;
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "rename",
                Message = $"{sessionId} is not open in a terminal, so there is nothing to rename in place. "
                    + (given is { Length: > 0 } && !string.Equals(next, name, StringComparison.Ordinal)
                        ? $"A name given here reaches no one; it will come back as \"{next}\" when it is next resumed."
                        : $"It will come back as \"{next}\" when it is next resumed."),
            });
        }

        var result = SessionNaming.RenameAsync(running, name, origin, store).GetAwaiter().GetResult();

        return Cli.EmitResult(new ActionResult
        {
            Ok = result.Ok,
            Action = "rename",
            Message = result.Ok ? $"{sessionId} {result.Message}." : $"Could not rename {sessionId} -- {result.Message}.",
            Items =
            [
                new ActionItem
                {
                    SessionId = sessionId,
                    Name = name,
                    Ok = result.Ok,
                    Message = result.Message,
                },
            ],
        });
    }

    /// <summary>
    /// The single file a prefix names. Same refusal to guess as <see cref="Resolve"/>, without
    /// its insistence that a session file exist at all -- `rename` can act on a running
    /// session whose transcript has not been written yet.
    /// </summary>
    private static FileInfo One(IReadOnlyList<FileInfo> matches, string prefix) =>
        matches.Count == 1
            ? matches[0]
            : throw new UsageException(
                $"'{prefix}' matches {matches.Count} sessions: "
                + string.Join(", ", matches.Take(5).Select(m => Path.GetFileNameWithoutExtension(m.Name)))
                + (matches.Count > 5 ? ", ..." : "") + ". Use a longer prefix.");

    /// <summary>Which session, and what to call it -- `--self` supplying the first.</summary>
    private static (string SessionId, string? Name) Target(Args args)
    {
        if (args.Has("self"))
        {
            var self = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            if (string.IsNullOrEmpty(self))
                throw new UsageException(
                    "--self needs CLAUDE_CODE_SESSION_ID, which Claude Code exports to every process "
                    + "it launches. This does not look like one of them.");

            return args.Positional.Count switch
            {
                0 => (self, null),
                1 => (self, args.Positional[0]),
                _ => throw new UsageException("'rename --self' takes at most one name. Quote a name with spaces."),
            };
        }

        return args.Positional.Count switch
        {
            1 => (args.Positional[0], null),
            2 => (args.Positional[0], args.Positional[1]),
            0 => throw new UsageException("'rename' needs a session id, or --self."),
            _ => throw new UsageException("'rename' takes a session id and at most one name. Quote a name with spaces."),
        };
    }

    /// <summary>The policy's answer, or an empty string when it would leave the name alone.</summary>
    private static (string Name, NameOrigin Origin) Decided(NameInputs inputs, NameStore store)
    {
        var decision = NamePolicy.Decide(inputs, store);
        return decision.HasName && decision.Origin is { } origin
            ? (decision.Name!, origin)
            : ("", NameOrigin.Floor);
    }

    // --- looking ------------------------------------------------------------

    /// <summary>
    /// What a live session's terminal is showing right now.
    ///
    /// Everything else this tool reads comes from the session file, which records the
    /// conversation and nothing else. The screen holds what the file cannot: the prompt a
    /// session is blocked on before it has written anything, a draft in its input box, the
    /// permission it is waiting to be granted. That is the difference between knowing a
    /// session is idle and knowing what it is idle *about*.
    ///
    /// It reads; it never types. Answering what is on screen is still `restart`'s kind of
    /// act — someone else's terminal — and stays a separate decision.
    /// </summary>
    public static int Peek(Args args)
    {
        args.RejectUnknown();

        // The id is checked before any scan: a mistyped command line should not cost a walk
        // of every project folder.
        var idOrPrefix = OneId(args, "peek");

        // The registry is the index here, not the projects folder. A session opened but not
        // yet typed into has written no file to resolve against, and that is precisely when
        // its screen is worth reading — a terminal sitting on a trust prompt records nothing
        // anywhere else. Every session this verb can work on is live by definition, so
        // nothing is lost by asking the shorter list first.
        var live = ResolveLive(idOrPrefix);
        if (live is null)
        {
            // Not live. Say which of the two reasons it is, since they need different
            // things of the operator: Resolve throws if there is no such session at all.
            var sleeping = Path.GetFileNameWithoutExtension(Resolve(RequireScanner(), idOrPrefix).Name);
            return Cli.EmitResult(new ActionResult
            {
                Ok = false,
                Action = "peek",
                Message = $"{sleeping} is not open in a terminal, so there is no screen to read.",
            });
        }

        var name = Titled(live.Name) ?? live.SessionId;
        var screen = ConsoleInput.ReadScreen(live.Pid);

        // No console to borrow. The registry knows why, and the reason is the useful half of
        // the answer: it is the same set of hosts a restart refuses to drive.
        if (screen.Length == 0)
            return Cli.EmitResult(new ActionResult
            {
                Ok = false,
                Action = "peek",
                Message = $"Could not read pid {live.Pid}'s console"
                    + (live.Entrypoint is { Length: > 0 } and not "cli"
                        ? $" — \"{name}\" is running under {live.Entrypoint} rather than a terminal of ours."
                        : $" — \"{name}\" may have just exited."),
            });

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "peek",
            Message = $"\"{name}\" (pid {live.Pid}) is showing {screen.Split('\n').Length} lines.",
            Screen = screen,
        });
    }

    // --- launching ----------------------------------------------------------

    /// <summary>
    /// Start a brand-new session: a terminal in a folder, sitting at a fresh
    /// <c>claude</c> prompt.
    ///
    /// Every other acting verb names the session it acts on. This one cannot — the id does
    /// not exist until the CLI writes its first record, long after this process has gone —
    /// so the result reports the folder and the command instead, and the session appears in
    /// `list` under its own id once it has something to say. There is likewise no
    /// already-open check to make: two sessions in one repo is a normal way to work, unlike
    /// two `--resume`s of the same conversation.
    ///
    /// The name is left to the CLI unless the caller supplies one. A new session has no
    /// title to be called by yet, so the folder-derived name is genuinely the best there is;
    /// the churn <see cref="SessionName"/> exists to prevent is a restart problem.
    /// </summary>
    public static int New(Args args)
    {
        args.RejectUnknown("in", "name", "trust", "dry-run");

        // The only positional this verb could plausibly be given is a session id, which
        // would mean the caller wanted `resume`. An unquoted multi-word --name lands here
        // too, so say both.
        if (args.Positional.Count > 0)
            throw new UsageException(
                $"'new' starts a session rather than naming one, so it takes no bare arguments (got '{args.Positional[0]}'). "
                + "Use --in <path> for the folder, quote a --name that has spaces, and `resume <id>` to reopen an existing session.");

        // No --in means here, which is what a person typing this inside a repo means.
        var folder = Path.GetFullPath(args.Has("in") ? args.Require("in") : Directory.GetCurrentDirectory());
        if (!Directory.Exists(folder))
            throw new UsageException($"No such folder: {folder}");

        var command = NewSessionLine(folder, args.Has("name") ? args.Require("name") : null);
        if (args.Has("dry-run"))
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "new",
                Message = $"Would run: {command}",
            });

        var launchedAt = DateTime.Now;
        StartTerminal(command);

        var opened = $"Opened a terminal running: {command}.";
        if (!args.Has("trust"))
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "new",
                Message = $"{opened} Its session id exists once you type something in it.",
            });

        // A folder Claude Code has not seen before stops on its trust prompt before it will
        // start a session at all, and nothing outside that terminal can see it happen: the
        // session is in no registry and has no file yet. So the wait is for a claude process
        // younger than this launch whose screen shows that dialog naming this folder, and
        // the answer goes only to a process where all three hold.
        var waiting = TrustPrompt.FindWaiting(folder, launchedAt, TrustWait, TrustPoll);
        if (waiting is null)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "new",
                Message = $"{opened} No trust prompt appeared within {TrustWait.TotalSeconds:0}s — either "
                    + "the folder was already trusted, or the terminal is showing something else.",
            });

        var answered = Answer(waiting.Value, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), dry: false);
        return Cli.EmitResult(new ActionResult
        {
            Ok = answered.Ok,
            Action = "new",
            Message = $"{opened} {answered.Message}",
            Screen = answered.Screen,
        });
    }

    /// <summary>
    /// How long <c>new --trust</c> waits for the dialog. Long enough for a cold start on a
    /// big repo, short enough that a folder which was already trusted does not hold the
    /// caller up for anything like a minute.
    /// </summary>
    private static readonly TimeSpan TrustWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrustPoll = TimeSpan.FromSeconds(1);

    /// <summary>The line a new session is launched with: into the folder, then Claude.</summary>
    internal static string NewSessionLine(string folder, string? name) =>
        $"cd {SessionName.Quote(folder)}; "
        + (name is { Length: > 0 } ? $"claude --name {SessionName.Quote(name)}" : "claude");

    // --- answering ----------------------------------------------------------

    /// <summary>
    /// Answer the trust prompt a session is sitting on, on the operator's behalf.
    ///
    /// This is the only verb that types an answer into a conversation rather than at the
    /// shell around it, so it is deliberately the narrowest one here: it presses Enter, on
    /// one dialog, and only when it can see that dialog with the yes option selected. The
    /// screen comes back either way — with the result when it acted, with the reason when
    /// it did not — because a caller told "no" needs to see what was there to decide.
    ///
    /// Why not simply press Enter: the dialog's second option is "No, exit". On a screen
    /// where the selection has moved, the same keystroke closes the session instead of
    /// trusting the folder.
    /// </summary>
    public static int Trust(Args args)
    {
        args.RejectUnknown("dry-run");

        var idOrPrefix = OneId(args, "trust");
        var live = ResolveLive(idOrPrefix);

        if (live is null)
        {
            var sleeping = Path.GetFileNameWithoutExtension(Resolve(RequireScanner(), idOrPrefix).Name);
            return Cli.EmitResult(new ActionResult
            {
                Ok = false,
                Action = "trust",
                Message = $"{sleeping} is not open in a terminal, so there is nothing to answer.",
            });
        }

        return Cli.EmitResult(Answer(live.Pid, Titled(live.Name) ?? live.SessionId, args.Has("dry-run")));
    }

    /// <summary>
    /// Read <paramref name="pid"/>'s screen, and press Enter only if it is showing the trust
    /// prompt with yes selected. Shared by <c>trust</c> and by <c>new --trust</c>.
    /// </summary>
    private static ActionResult Answer(int pid, string name, bool dry)
    {
        var screen = ConsoleInput.ReadScreen(pid);

        switch (TrustPrompt.Read(screen))
        {
            case TrustPrompt.State.NotShowing:
                return new ActionResult
                {
                    Ok = false,
                    Action = "trust",
                    Message = screen.Length == 0
                        ? $"Could not read pid {pid}'s console, so nothing was typed."
                        : $"\"{name}\" is not at a trust prompt. Nothing was typed.",
                    Screen = screen.Length == 0 ? null : screen,
                };

            case TrustPrompt.State.OtherSelected:
                return new ActionResult
                {
                    Ok = false,
                    Action = "trust",
                    Message = $"\"{name}\" is at the trust prompt, but the selection has moved off "
                        + "\"Yes, I trust this folder\" — Enter would take the other option, which "
                        + "closes it. Nothing was typed.",
                    Screen = screen,
                };
        }

        if (dry)
            return new ActionResult
            {
                Ok = true,
                Action = "trust",
                Message = $"Would press Enter on the trust prompt in \"{name}\" (pid {pid}).",
                Screen = screen,
            };

        if (!TrustPrompt.Accept(pid))
            return new ActionResult
            {
                Ok = false,
                Action = "trust",
                Message = $"Could not type into pid {pid}'s console.",
                Screen = screen,
            };

        // Reported done only once the dialog is gone, the way a restart is reported done
        // only once the session says it is back.
        Thread.Sleep(1500);
        var after = ConsoleInput.ReadScreen(pid);
        bool answered = TrustPrompt.Read(after) == TrustPrompt.State.NotShowing;

        return new ActionResult
        {
            Ok = answered,
            Action = "trust",
            Message = answered
                ? $"Trusted the folder for \"{name}\" (pid {pid}); it is past the prompt."
                : $"Pressed Enter for \"{name}\" (pid {pid}), but the prompt is still up — look at the screen.",
            Screen = after,
        };
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
    /// <summary>
    /// The live session an id or prefix names — the registry first, the scanner second.
    ///
    /// <see cref="Resolve"/> answers from the files on disk, which is right for every verb
    /// that reads a conversation and wrong for one that only has to reach a process: a
    /// terminal opened and never prompted is running, is closable, and is in no file.
    /// </summary>
    private static LiveSession ResolveLive(
        SessionScanner scanner, Dictionary<string, List<LiveSession>> live, string idOrPrefix)
    {
        var matches = live
            .Where(kv => kv.Key.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value)
            .ToList();

        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            throw new UsageException(
                $"'{idOrPrefix}' matches {matches.Count} running sessions. Use a longer prefix.");

        // Nothing running under that id: say whether it exists at all, which is the more
        // useful half of the answer. Resolve throws its own message when it does not.
        var file = Path.GetFileNameWithoutExtension(Resolve(scanner, idOrPrefix).Name);
        throw new UsageException($"'{file}' is not open in a terminal.");
    }

    private static bool SessionFileExists(SessionScanner scanner, string sessionId)
    {
        try { return scanner.FindByPrefix(sessionId).Count == 1; }
        catch { return false; }
    }

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

    /// <summary>
    /// The live session an id or unique prefix names, or null when none is running. Same
    /// prefix rule as <see cref="Resolve"/> and the same refusal to guess between two.
    /// </summary>
    private static LiveSession? ResolveLive(string idOrPrefix)
    {
        var matches = LiveSessions.Scan()
            .Where(e => e.Key.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Value)
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new UsageException(
                $"'{idOrPrefix}' matches {matches.Count} running sessions: "
                + string.Join(", ", matches.Take(5).Select(m => m.SessionId))
                + ". Use a longer prefix."),
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

    /// <summary>Open a terminal and run a command in it.</summary>
    private static void StartTerminal(string command) => TerminalLauncher.Start(command);
}
