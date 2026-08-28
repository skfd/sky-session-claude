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

        // "Still on the hook" is the question --unfinished answers, so an abandoned session
        // is out even though it is not Settled: the operator already said they are not going
        // back, and the app's list hides it for the same reason. Its Status stays whatever it
        // earned — --disposition abandoned is how you ask for those back.
        if (args.Has("unfinished") && (row.Settled || row.Disposition == "abandoned")) return false;
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
        args.RejectUnknown("stale", "host", "yes", "force", "dry-run");

        var scanner = RequireScanner();
        var installed = ClaudeInstall.InstalledVersion;
        var live = LiveSessions.Scan();
        bool dry = args.Has("dry-run");

        // What a session comes back under is the policy's call, so each target carries what
        // the policy needs rather than a title this verb would have to interpret itself.
        List<(LiveSession Live, SessionStatus? Tail, string Name, NameInputs Inputs)> targets;
        List<ActionItem> skipped = new();

        // Hosts join the sweep but not the list above: a host is not a session, has no id and
        // no name to come back under, and what puts it back is its own command line.
        List<RemoteControlHost> hostTargets = new();

        var names = new NameStore();
        var liveNames = SessionNaming.LiveNamesOf(live);

        if (args.Has("host"))
        {
            // You pointing at one host, which is the same shape as `restart <id>`: it acts on
            // the spot rather than stating a plan, it does not care whether the host is behind
            // — pointing at it says you want it back up — and it proceeds on anything the
            // policy merely wants to ask about. Only what cannot be done safely is refused.
            if (args.Positional.Count > 0)
                throw new UsageException(
                    $"'restart --host' names the folder itself, so it takes no session ids (got '{args.Positional[0]}').");

            var wanted = args.Require("host");
            var infos = scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue })
                .ToDictionary(i => i.SessionId, StringComparer.OrdinalIgnoreCase);

            var hosts = RemoteControlHosts.FromScan(infos.Values).ToList();
            var host = OneHost(hosts, wanted);

            var tree = ProcessTree.Snapshot();
            var serving = RemoteControlHosts.Serving(host, live.Values.SelectMany(v => v), tree.Parents)
                .Select(s => new HostRestartPolicy.Served(s, infos.GetValueOrDefault(s.SessionId)?.Status))
                .ToList();

            targets = new List<(LiveSession, SessionStatus?, string, NameInputs)>();

            if (!args.Has("force") && serving.Any(s => IsSelf(s.Live.SessionId)))
                skipped.Add(HostSkip(host,
                    "it is serving the session this command is running in — pass --force if you mean it"));
            else
            {
                var verdict = HostRestartPolicy.Judge(
                    host, serving, RemoteControlHosts.ConversationsUnder(host.Pid, tree.Children), DateTime.Now);

                if (verdict.Safety == SweepSafety.Unsafe) skipped.Add(HostSkip(host, verdict.Reason));
                else hostTargets.Add(host);
            }
        }
        else if (args.Has("stale"))
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

            // Now the hosts. They go stale the same way and are the ones most likely to be
            // far behind — a host is started once and then sits there for days — but they can
            // never say so themselves: no registry entry means no build to compare, so the
            // process image is the signal (ClaudeInstall.IsSuperseded). They come out of the
            // same scan the sessions did, because that is what knows the real folder each one
            // is serving.
            var tree = ProcessTree.Snapshot();
            var running = live.Values.SelectMany(v => v).ToList();

            foreach (var host in RemoteControlHosts.FromScan(infos.Values))
            {
                if (!host.Stale) continue;

                // What a host is serving, one level down: the sessions it spawned are its
                // children in the process tree, and they are where all the state lives.
                var serving = RemoteControlHosts.Serving(host, running, tree.Parents)
                    .Select(s => new HostRestartPolicy.Served(s, infos.GetValueOrDefault(s.SessionId)?.Status))
                    .ToList();

                // The self-guard reaches one level further than it does for sessions: quitting
                // a host takes down the conversation this command is running in with it.
                if (!args.Has("force") && serving.Any(s => IsSelf(s.Live.SessionId)))
                {
                    skipped.Add(HostSkip(host, "it is serving the session this command is running in"));
                    continue;
                }

                var hostVerdict = HostRestartPolicy.Judge(
                    host, serving, RemoteControlHosts.ConversationsUnder(host.Pid, tree.Children), DateTime.Now);

                if (!hostVerdict.CanSweep) skipped.Add(HostSkip(host, hostVerdict.Reason));
                else hostTargets.Add(host);
            }

            // The sweep drives terminals nobody is looking at, so it states its plan and
            // waits to be told twice. A single named session does not need that.
            if (!args.Has("yes")) dry = true;
        }
        else
        {
            if (args.Positional.Count == 0)
                throw new UsageException("'restart' needs a session id, or --stale, or --host <project>.");

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

        foreach (var host in hostTargets)
        {
            if (dry)
            {
                items.Add(HostItem(host, true,
                    $"would restart its host: {LaunchLine.HostAgain(host.Folder, host.CommandLine)}"));
                continue;
            }

            var result = HostRestarter.RestartAsync(host).GetAwaiter().GetResult();
            if (result.Ok) done++;
            items.Add(HostItem(host, result.Ok, $"its host: {result.Message}"));
        }

        items.AddRange(skipped);

        int attempted = targets.Count + hostTargets.Count;
        var what = (targets.Count, hostTargets.Count) switch
        {
            // Naming a host and having it refused is still a sentence about hosts.
            (0, 0) when args.Has("host") => "0 Remote Control host(s)",
            (0, > 0) => $"{hostTargets.Count} Remote Control host(s)",
            (_, 0) => $"{targets.Count} session(s)",
            _ => $"{targets.Count} session(s) and {hostTargets.Count} Remote Control host(s)",
        };

        var message = dry
            ? $"Would restart {what}"
                + (skipped.Count > 0 ? $"; skipping {skipped.Count}" : "")
                + (args.Has("stale") && !args.Has("yes") ? ". Re-run with --yes to do it." : ".")
            : $"Restarted {done} of {attempted}"
                + (skipped.Count > 0 ? $"; skipped {skipped.Count}" : "") + ".";

        // Naming a session and getting nothing is a failure the caller should see in the
        // exit code. A sweep skipping some is not — reporting what it left is the job.
        bool ok = dry
            || (args.Has("stale") ? done == attempted
                                  : done == attempted && skipped.Count == 0);

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
                if (ResolveLive(scanner, live, id) is not { } session)
                {
                    // Reported, not thrown: `close a b c` run twice is the natural thing to
                    // do after a partial one, and the second run should say which are already
                    // gone rather than abandon the ids that are still up.
                    var gone = scanner.BuildRow(Resolve(scanner, id), SessionFileParser.DefaultContextWindow);
                    skipped.Add(Skip(gone.SessionId, gone.Name ?? gone.SessionId,
                        "it is not open in a terminal"));
                    continue;
                }

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

    /// <summary>
    /// The one host <c>--host</c> names, by folder or by project.
    ///
    /// A folder is matched whole and a project name exactly before either falls back to a
    /// substring, so a repo whose name contains another's cannot be shadowed by it. Nothing
    /// found and more than one found are both usage errors: this verb quits a server, and
    /// quitting the wrong one is not a mistake to make on a guess.
    /// </summary>
    private static RemoteControlHost OneHost(IReadOnlyList<RemoteControlHost> hosts, string wanted)
    {
        var found = RemoteControlHosts.Matching(hosts, wanted);

        if (found.Count == 0)
            throw new UsageException(hosts.Count == 0
                ? "No folder has a Remote Control host running. `standby` is what starts them."
                : $"No Remote Control host matches '{wanted}'. Running now: "
                    + string.Join(", ", hosts.Select(h => h.Project).Order()) + ".");

        if (found.Count > 1)
            throw new UsageException($"'{wanted}' matches {found.Count} hosts: "
                + string.Join(", ", found.Select(h => h.Project).Order())
                + ". Name one of them, or give the folder.");

        return found[0];
    }

    /// <summary>
    /// A host's row. No id, the same way <c>standby</c>'s rows have none — a host is not a
    /// session — so the folder is what identifies it.
    /// </summary>
    private static ActionItem HostItem(RemoteControlHost host, bool ok, string message) => new()
    {
        SessionId = "",
        Name = host.Project,
        Folder = host.Folder,
        Ok = ok,
        Message = message,
    };

    private static ActionItem HostSkip(RemoteControlHost host, string why) =>
        HostItem(host, false, $"skipped its host — {why}");

    // --- resuming -----------------------------------------------------------

    public static int Resume(Args args)
    {
        args.RejectUnknown("dry-run", "force", "remote-control", "rc");

        var scanner = RequireScanner();
        var file = Resolve(scanner, OneId(args, "resume"));
        var info = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow);

        if (string.IsNullOrEmpty(info.Command))
            throw new UsageException($"{info.SessionId} has no resumable command (no recorded cwd).");

        bool force = args.Has("force");
        bool dry = args.Has("dry-run");
        var label = info.Name ?? info.SessionId;

        if (force && IsSelf(info.SessionId))
            throw new UsageException(
                "That is the session this command is running in; force-resuming it would kill this "
                + "process mid-sentence. Do it from the app or another session.");

        // Who has it, by registry and by command line both. The command line is what makes
        // this honest: a session that hung before registering holds a terminal that the
        // registry knows nothing about, and saying "not open" about it is how one gets
        // stranded with no way back.
        var holders = SessionReviver.Holders(info.SessionId);

        if (holders.Count > 0 && !force)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "resume",
                Message = Held(label, holders),
                Items = holders.Select(h => new ActionItem
                {
                    SessionId = info.SessionId,
                    Name = label,
                    Ok = true,
                    Message = h.Registered
                        ? $"pid {h.Pid} is running it"
                        : $"pid {h.Pid} is running it but never registered — it may be stuck starting up",
                }).ToList(),
            });

        // What reopening this means is SessionResume's call, not this verb's: the app runs the
        // same decision when a skysession://resume link is clicked, and a second copy of it
        // here is how the two would drift on the thing that matters — the name, which comes
        // from the policy rather than from whoever is composing the launch.
        //
        // Only the command is taken. Whether something already holds the session is decided
        // above by SessionReviver, which sees a terminal the registry does not.
        var command = SessionResume.Plan(info, new NameStore(), dry).Command!;

        if (dry)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "resume",
                Message = holders.Count == 0
                    ? $"Would run: {command}"
                    : $"Would end {Listed(holders)}, then run: {command}",
            });

        if (holders.Count == 0)
        {
            StartTerminal(command);
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "resume",
                Message = $"Opened a terminal running: {command}",
            });
        }

        var result = SessionReviver.Revive(info.SessionId, command, holders);
        return Cli.EmitResult(new ActionResult
        {
            Ok = result.Ok,
            Action = "resume",
            Message = result.Ok ? result.Message : $"Could not force-resume \"{label}\": {result.Message}.",
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

        // Having a model read the session is the one thing here with a real cost -- not a bill
        // (headless Claude Code bills nothing per call under a subscription login) but a slice
        // of the account's rate-limit window and ten-odd seconds of waiting. So it happens
        // because it was asked for, and `--ask` on a session a free source could have named is
        // refused rather than quietly spent.
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

    /// <summary>
    /// How a session is being held, said the way it matters: "already open" is a reason to
    /// leave it alone, "running but never registered" is a reason to reach for --force.
    /// </summary>
    private static string Held(string name, IReadOnlyList<SessionHolder> holders) =>
        holders.All(h => h.Registered)
            ? $"\"{name}\" is already open in a terminal ({Listed(holders)}). "
              + "Add --force to end it and resume."
            : $"\"{name}\" is running ({Listed(holders)}) but never registered — it may be stuck "
              + "starting up. Add --force to end it and resume.";

    private static string Listed(IReadOnlyList<SessionHolder> holders) =>
        holders.Count == 1 ? $"pid {holders[0].Pid}" : "pids " + string.Join(", ", holders.Select(h => h.Pid));

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
        args.RejectUnknown("in", "name", "trust", "dry-run", "remote-control", "rc");

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

    /// <summary>
    /// The line a new session is launched with: into the folder, then Claude.
    ///
    /// Composed by <see cref="LaunchLine"/>, which is also what the app uses when a
    /// <c>skysession://new</c> link is clicked, so a folder with an apostrophe in it is
    /// quoted the same way whichever front end opened it.
    ///
    /// It used to take a <c>remoteControl</c> flag, and no longer does, because every launch
    /// carries it now — see <see cref="ClaudeLaunch"/>. <c>--rc</c> and <c>--remote-control</c>
    /// are still accepted so nothing that passes them breaks; they ask for what already
    /// happens.
    ///
    /// A name never rides on <c>--remote-control</c>, even though that flag accepts one. The
    /// two are separate flags inside the CLI and only <c>--name</c> reaches the registry —
    /// see <see cref="RestartPolicy.ResumeCommand"/>, which learned the same thing on the way
    /// back up from a restart.
    /// </summary>
    internal static string NewSessionLine(string folder, string? name) =>
        LaunchLine.NewIn(folder, name);

    // --- links --------------------------------------------------------------

    /// <summary>
    /// Write a <c>skysession://</c> link for a session, or for starting one in a folder.
    ///
    /// The producer half of the link feature, and it exists before anything consumes links
    /// so that whatever writes them — the morning brief, a note, an agent handing over an
    /// offer rather than asking a question — has something to call.
    ///
    /// It checks what the handler will check, and refuses now rather than at the click. A
    /// link whose id matches nothing, or whose folder no link may open, is worse than no
    /// link: it is written into a document that outlives this command, and the person who
    /// finds out is the one who clicked it.
    ///
    /// The full id goes into the link even when a prefix was typed. A prefix that is unique
    /// today can be ambiguous next month, and the link is the thing that lasts.
    /// </summary>
    public static int Link(Args args)
    {
        args.RejectUnknown("done", "new");

        if (args.Has("new"))
        {
            if (args.Positional.Count > 0)
                throw new UsageException(
                    $"'link --new' takes a folder, not a session id (got '{args.Positional[0]}'). "
                    + "Use `link <id>` for a session.");

            var roots = LinkRoots.Load();
            var folder = Path.GetFullPath(args.Require("new"));

            // Typed as a real folder, written as a relative one. Whoever runs this has an
            // absolute path in hand because it is what they are looking at; the link must
            // not carry one, so the translation happens here rather than being asked of them.
            if (roots.Relative(folder) is not { } relative)
                return Cli.EmitResult(new ActionResult
                {
                    Ok = false,
                    Action = "link",
                    Message = $"{folder} is not under a folder links may open sessions in."
                        + (roots.Warning is { Length: > 0 } rw ? $"  ({rw})" : "")
                        + $"  Those are configured in {LinkRoots.DefaultPath()}.",
                });

            var url = $"{SessionUri.Scheme}://new?in={Uri.EscapeDataString(relative)}";

            // Parsed back rather than trusted: the link is checked by the same code the
            // handler runs, so "this will work when clicked" is a fact rather than a hope.
            var check = SessionUri.Parse(url, roots.Roots);
            if (!check.Ok)
                return Cli.EmitResult(new ActionResult
                {
                    Ok = false,
                    Action = "link",
                    Message = check.Refusal!
                        + (roots.Warning is { Length: > 0 } w ? $"  ({w})" : "")
                        + $"  Folders links may open are configured in {LinkRoots.DefaultPath()}.",
                });

            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "link",
                Message = url,
                Items = [new ActionItem { SessionId = "", Ok = true, Message = folder, Folder = folder, Link = url }],
            });
        }

        var scanner = RequireScanner();
        var file = Resolve(scanner, OneId(args, "link"));
        var info = scanner.BuildRow(file, SessionFileParser.DefaultContextWindow);
        var verb = args.Has("done") ? "done" : "resume";
        var link = $"{SessionUri.Scheme}://{verb}/{info.SessionId}";

        return Cli.EmitResult(new ActionResult
        {
            Ok = true,
            Action = "link",
            Message = link,
            Items = [new ActionItem
            {
                SessionId = info.SessionId,
                Ok = true,
                Name = info.Name,
                Message = $"{verb} \"{info.Name ?? info.SessionId}\"",
                Link = link,
            }],
        });
    }

    // --- standby ------------------------------------------------------------

    /// <summary>
    /// Leave a Remote Control host running in every project worked in lately.
    ///
    /// This is the verb for walking away from the desk, and it exists because of an asymmetry:
    /// Remote Control is per process, so a project with nothing running is a project a phone
    /// cannot open — and a phone cannot start one either. Everything else here can be done when
    /// you get back. This one has to happen before you leave.
    ///
    /// What it starts is <c>claude rc</c>, the host, rather than <c>claude --remote-control</c>,
    /// a bridged interactive session. The host pre-creates one session so there is a row on the
    /// phone immediately and then spawns more on demand, which matters because second thoughts
    /// are what phones are for: a session per project caps you at one conversation per repo,
    /// and starting another is precisely the thing a phone cannot do for itself. The cost is
    /// that a host has no terminal you can type into at the desk, and that what it spawns is
    /// <c>sdk-cli</c> — the kind <see cref="ClosePolicy"/> refuses to sweep, on purpose.
    ///
    /// The same distinction decides what it passes over: only a live host means a folder is
    /// already on standby. A bridged terminal there is reachable from a phone but cannot be
    /// asked for a second conversation, which is the whole thing standby is there to provide.
    ///
    /// Like the other sweeps it drives real terminals, so it states its plan and waits to be
    /// told twice. Unlike them, nothing it does can lose work — everything it touches is
    /// something it just made — so the second telling is about the terminals about to appear on
    /// your desktop, not about anything at risk.
    /// </summary>
    public static int Standby(Args args)
    {
        args.RejectUnknown("in", "since", "recent", "yes", "dry-run");

        if (args.Positional.Count > 0)
            throw new UsageException(
                $"'standby' finds its own projects, so it takes no bare arguments (got '{args.Positional[0]}'). "
                + "Use --in <path> for one folder, or --since <span> for how far back to look.");

        var now = DateTime.Now;
        var window = args.Span("since", SessionCore.Standby.DefaultWindow);
        StandbyPlan plan;
        var untrustedNote = "";

        if (args.Has("in"))
        {
            if (args.Has("since") || args.Has("recent"))
                throw new UsageException(
                    "'standby --in' names the folder itself, so it takes neither --since nor --recent.");

            var folder = Path.GetFullPath(args.Require("in"));
            if (!Directory.Exists(folder))
                throw new UsageException($"No such folder: {folder}");

            // Named rather than found, so recency and the .git rule have nothing to say about
            // it — pointing at a folder is a better answer than any rule about it. What still
            // runs is the check that matters from a phone: a second host in a repo that already
            // has one is two identical rows in a list that shows no folders.
            var project = SessionCore.Standby.ProjectOf(folder);
            var projectDir = new SessionScanner().ProjectDirFor(folder);

            if (RemoteControlHosts.ServingFrom(projectDir) is { } host)
                return Cli.EmitResult(new ActionResult
                {
                    Ok = true,
                    Action = "standby",
                    Message = $"{project} is {SessionCore.Standby.AlreadyReason(host)}.",
                    Items = [new ActionItem
                    {
                        SessionId = "",
                        Name = project,
                        Folder = folder,
                        Ok = false,
                        Message = $"skipped — {SessionCore.Standby.AlreadyReason(host)}",
                    }],
                });

            plan = new StandbyPlan
            {
                Open = [new StandbyTarget { Folder = folder, Project = project, LastActive = now }],
                Skipped = [],
            };
        }
        else
        {
            var scanner = RequireScanner();
            plan = SessionCore.Standby.Decide(
                scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue }),
                now, window, args.Int("recent", int.MaxValue));

            // Standby is where a folder Claude Code has never been trusted with shows up, and
            // `claude rc` will not ask: it says to run `claude` there first and stops. Nothing
            // outside that terminal can see it happen, so this is said rather than detected.
            if (plan.Open.Count > 0) untrustedNote =
                " A folder Claude Code has not been trusted with will not start a host — run"
                + " `claude` there once to answer the trust prompt.";
        }

        // No --yes is a plan, the same as the other sweeps; --dry-run says so outright.
        bool dry = args.Has("dry-run") || !args.Has("yes");

        var items = new List<ActionItem>();
        foreach (var target in plan.Open)
        {
            // The project is the name prefix, not a session name: what this starts is a host,
            // and everything it goes on to create is named after the prefix. Left off, they
            // would all be named after this machine instead — see ClaudeLaunch.Host.
            var command = LaunchLine.HostIn(target.Folder, target.Project);
            if (!dry) StartTerminal(command);

            items.Add(new ActionItem
            {
                // No id, and further from having one than `new` is: a host is not a session at
                // all, and the sessions it pre-creates and spawns are its business. The folder
                // is what identifies the row.
                SessionId = "",
                Name = target.Project,
                Folder = target.Folder,
                Ok = true,
                Message = dry ? $"would run: {command}" : $"opened a terminal running: {command}",
            });
        }

        foreach (var skip in plan.Skipped)
            items.Add(new ActionItem
            {
                SessionId = "",
                Name = skip.Project,
                Folder = skip.Folder,
                Ok = false,
                Message = $"skipped — {skip.Reason}",
            });

        var named = string.Join(", ", plan.Open.Select(t => t.Project));
        var also = plan.Skipped.Count > 0 ? $"; skipping {plan.Skipped.Count}" : "";

        string message;
        if (plan.Open.Count == 0)
            message = plan.Skipped.Count > 0
                ? $"Nothing to put on standby — all {plan.Skipped.Count} project(s) found were passed over."
                : $"No project has been worked in within {Spell(window)}. Widen it with --since 30d.";
        else if (dry)
            message = $"Would put {plan.Open.Count} project(s) on standby: {named}{also}."
                + (args.Has("dry-run") ? "" : " Re-run with --yes to open them.")
                + untrustedNote;
        else
            message = $"{plan.Open.Count} project(s) on standby: {named}{also}."
                + " Each is a claude rc host: one session ready on your phone now, more when you"
                + " start them. Give them a moment to connect."
                + untrustedNote;

        return Cli.EmitResult(new ActionResult
        {
            // A sweep that reports what it passed over has done its job; only a usage error
            // makes this verb fail, and that has already thrown by here.
            Ok = true,
            Action = "standby",
            Message = message,
            Items = items,
        });
    }

    /// <summary>How a span reads back to the person who typed it.</summary>
    private static string Spell(TimeSpan window) =>
        window.TotalDays >= 1 ? $"{window.TotalDays:0.#} day(s)"
        : window.TotalHours >= 1 ? $"{window.TotalHours:0.#} hour(s)"
        : $"{window.TotalMinutes:0.#} minute(s)";

    /// <summary>
    /// Whether the caller asked for a session their phone can reach. Spelled either way,
    /// because <c>--rc</c> is what it is called out loud and <c>--remote-control</c> is what
    /// Claude Code itself calls it.
    /// </summary>
    private static bool WantsRemoteControl(Args args) =>
        args.Has("remote-control") || args.Has("rc");

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

    // --- the inbox ----------------------------------------------------------

    /// <summary>How long a queued command stays runnable. See the age gate below.</summary>
    private const int DefaultMaxAgeMinutes = 120;

    /// <summary>
    /// Run the commands the brief queued, then get the file out of the way.
    ///
    /// This is the one verb whose caller is not in the room. Everything else here is typed
    /// by someone watching a terminal, who sees the answer and can undo it; this arrives
    /// from a folder, minutes or hours after it was decided, and runs against sessions the
    /// decider could not see the current state of. Three things follow from that, and they
    /// are the whole design:
    ///
    /// <list type="bullet">
    /// <item><b>Nothing runs twice.</b> The file is moved aside before the results are
    ///       written, so a task that fires every minute finds an empty inbox on the second
    ///       minute rather than resuming everything again.</item>
    /// <item><b>Nothing runs late.</b> A queue older than <c>--max-age</c> is refused
    ///       whole. The failure this prevents is specific: the machine is off when the
    ///       brief writes, and a week later a boot opens six terminals for decisions made
    ///       about sessions that have all moved on.</item>
    /// <item><b>Nothing is trusted.</b> No command carries <c>--force</c> or <c>--trust</c>,
    ///       and <c>new</c> may only start in a folder that already has sessions in it —
    ///       an allowlist nobody has to maintain, because it is a list of places you have
    ///       already worked.</item>
    /// </list>
    /// </summary>
    public static int Inbox(Args args)
    {
        args.RejectUnknown("run", "max-age", "dry-run");

        var input = args.Require("run");
        bool dry = args.Has("dry-run");

        // The ordinary case, several hundred times a day: nobody queued anything. That is
        // success, not an error — a scheduled task that logged a failure every minute for
        // being asked to do nothing would be turned off within a week.
        if (!File.Exists(input))
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "inbox",
                Message = $"Nothing queued at {input}.",
            });

        var (spent, resultPath) = InboxFile.Paths(input);
        InboxFile.Parsed queue;
        try
        {
            queue = InboxFile.Read(File.ReadAllText(input));
        }
        catch (InboxFile.RejectedException e)
        {
            // A file we cannot read is still moved aside. Left in place it would be re-read
            // and re-rejected every minute until someone noticed, and the report of what was
            // wrong with it would scroll past a hundred times over.
            if (!dry) Move(input, spent);
            return Cli.EmitResult(new ActionResult
            {
                Ok = false,
                Action = "inbox",
                Message = $"Rejected the queue at {input} — {e.Message}  Moved to {spent}.",
            });
        }

        var issued = queue.IssuedAt ?? new DateTimeOffset(File.GetLastWriteTime(input));
        var age = DateTimeOffset.Now - issued;
        var maxAge = TimeSpan.FromMinutes(args.Int("max-age", DefaultMaxAgeMinutes));
        if (age > maxAge)
        {
            if (!dry) Move(input, spent);
            return Cli.EmitResult(new ActionResult
            {
                Ok = false,
                Action = "inbox",
                Message = $"Refused {queue.Commands.Count} command(s): the queue was written "
                    + $"{Ago(age)} and only stays runnable for {maxAge.TotalMinutes:0} minutes. "
                    + $"Moved to {spent}.",
            });
        }

        var items = queue.Commands.Select(c => Run(c, dry)).ToList();
        int ok = items.Count(i => i.Ok);

        var result = new ActionResult
        {
            Ok = items.All(i => i.Ok),
            Action = "inbox",
            Message = dry
                ? $"Would run {items.Count} command(s) from {input}."
                : $"Ran {items.Count} command(s) from {input}: {ok} ok, {items.Count - ok} failed.",
            Items = items,
        };

        if (!dry)
        {
            // Move first, write second. If writing the result throws, the worst case is a
            // run nobody hears about; the other order's worst case is a run that happens
            // again next minute.
            Move(input, spent);
            Cli.Emit(result, resultPath);
        }

        return Cli.EmitResult(result);
    }

    /// <summary>
    /// One queued command, run through the very verb a person would have typed.
    /// </summary>
    private static ActionItem Run(InboxFile.Entry entry, bool dry)
    {
        var flags = dry ? new[] { "--dry-run" } : [];

        Args Built(params string[] positional) =>
            new(entry.Action, positional.Concat(flags));

        Func<int> verb;
        switch (entry.Action)
        {
            case "new":
                if (entry.In is not { Length: > 0 } folder)
                    return Failed(entry, "'new' needs a folder — give it \"in\".");
                if (KnownFolder(folder) is not { } known)
                    return Failed(entry,
                        $"\"{folder}\" has no Claude sessions in it. The inbox only starts sessions "
                        + "in folders you have already worked in, so a queue cannot point one "
                        + "somewhere new.");

                var newArgs = new List<string> { "--in", known };
                if (entry.Name is { Length: > 0 } n) { newArgs.Add("--name"); newArgs.Add(n); }
                newArgs.AddRange(flags);
                verb = () => New(new Args("new", newArgs));
                break;

            case "resume":
            case "restart":
                if (entry.Id is not { Length: > 0 } id)
                    return Failed(entry, $"'{entry.Action}' needs a session id.");
                verb = entry.Action == "resume"
                    ? () => Resume(Built(id))
                    : () => Restart(Built(id));
                break;

            default:
                if (entry.Id is not { Length: > 0 } markId)
                    return Failed(entry, $"'{entry.Action}' needs a session id.");
                var disposition = entry.Action switch
                {
                    "done" => Disposition.Done,
                    "abandon" => Disposition.Abandoned,
                    _ => Disposition.None,
                };
                verb = () => Mark(Built(markId), disposition);
                break;
        }

        var outcome = Cli.Capture(entry.Action, verb);

        // A verb's headline counts what it did; the reason it did not do the rest lives in
        // its items. Dropping those would leave the brief reporting "restart 0, skipping 1"
        // tomorrow morning with no way to tell whether that was a session mid-turn, a
        // question waiting on an answer, or this very process declining to kill itself.
        var detail = outcome.Items is { Count: > 0 } inner
            ? " — " + string.Join("; ", inner.Select(i => i.Message))
            : "";

        return new ActionItem
        {
            SessionId = entry.Id ?? entry.In ?? "",
            Ok = outcome.Ok,
            Message = $"{entry.Action}: {outcome.Message}{detail}",
            Name = outcome.Items?.FirstOrDefault()?.Name,
        };
    }

    private static ActionItem Failed(InboxFile.Entry entry, string why) => new()
    {
        SessionId = entry.Id ?? entry.In ?? "",
        Ok = false,
        Message = $"{entry.Action}: {why}",
    };

    /// <summary>
    /// The folder as this machine spells it, or null if no session has ever run there.
    ///
    /// This is the allowlist for <c>new</c>, and it costs nothing to keep: the set of
    /// folders you have worked in is already recorded, one <c>cwd</c> per session. Matching
    /// against it means a queue can open a session in any of your repos and in none of the
    /// places that are not — no configuration, and nothing to forget to update when a repo
    /// is added.
    /// </summary>
    private static string? KnownFolder(string folder)
    {
        string wanted;
        try { wanted = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch (Exception) { return null; }

        return RequireScanner()
            .Scan(new ScanOptions { All = true, Top = int.MaxValue })
            .Select(info => info.Cwd)
            .OfType<string>()
            .FirstOrDefault(cwd => string.Equals(
                cwd.TrimEnd('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static void Move(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (IOException) { /* someone is holding it; the age gate stops it running twice */ }
    }

    private static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 90 => $"{age.TotalMinutes:0} minutes ago",
        < 48 * 60 => $"{age.TotalHours:0} hours ago",
        _ => $"{age.TotalDays:0} days ago",
    };

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
    /// <returns>
    /// Null when nothing is running under that id, which is a per-id skip rather than a usage
    /// error — an id that names no session at all is still the caller mistyping, and
    /// <see cref="Resolve"/> says so.
    /// </returns>
    private static LiveSession? ResolveLive(
        SessionScanner scanner, Dictionary<string, List<LiveSession>> live, string idOrPrefix)
    {
        var matches = live
            .Where(kv => kv.Key.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new UsageException(
                $"'{idOrPrefix}' matches {matches.Count} running sessions. Use a longer prefix."),
        };
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
