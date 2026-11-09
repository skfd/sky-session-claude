using SessionCore;

namespace SessionCli;

internal static partial class Commands
{
    /// <summary>
    /// <c>project &lt;standing&gt; &lt;name|glob|path&gt;... [--note]</c> — set whether a whole
    /// project is still worked on. Bare <c>project</c> lists what has been set aside.
    ///
    /// Names are matched against the folders under the link roots (~/Code by default) first,
    /// because that is where projects live and listing one directory is instant; only a name
    /// that matches nothing there costs a scan of the session history, for a project that
    /// lives somewhere else.
    /// </summary>
    public static int Project(Args args)
    {
        args.RejectUnknown("note", "dry-run");

        var store = new ProjectStandingStore();

        if (args.Positional.Count == 0)
            return Cli.EmitResult(new ActionResult
            {
                Ok = true,
                Action = "project",
                Message = store.All.Count == 0
                    ? "Every project is active; nothing has been set aside."
                    : $"{store.All.Count} project(s) set aside.",
                Items = store.All
                    .OrderBy(kv => kv.Value.Standing)
                    .ThenBy(kv => SessionCore.Standby.ProjectOf(kv.Key), StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new ActionItem
                    {
                        SessionId = "",
                        Ok = true,
                        Name = SessionCore.Standby.ProjectOf(kv.Key),
                        Folder = kv.Key,
                        Message = ProjectStanding.ToWire(kv.Value.Standing)
                                  + (kv.Value.Note is { } n ? $" — {n}" : "")
                                  + $" (since {kv.Value.At:yyyy-MM-dd})",
                    })
                    .ToList(),
                Warning = store.LoadWarning,
            });

        var word = args.Positional[0];
        if (ProjectStanding.FromWire(word) is not { } standing)
            throw new UsageException(
                $"'{word}' is not a standing. Try one of: {string.Join(", ", ProjectStanding.Words)}.");

        var patterns = args.Positional.Skip(1).ToList();
        if (patterns.Count == 0)
            throw new UsageException(
                $"'project {word}' needs the project(s): a name, a glob like experiment-*, or a path.");

        var note = args.Value("note") is { } raw && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;
        if (standing == Standing.Parked && note is null)
            throw new UsageException(
                "'project parked' needs --note: why it is set aside, and what would wake it. "
                + "A parked project with no note is just a forgotten one with a label.");

        var resolution = StandingTargets.Resolve(patterns, RootFolders());
        if (resolution.Unmatched.Count > 0)
        {
            // The slow path, only when it is needed: folders sessions ran in, wherever they are.
            var scanner = RequireScanner();
            var elsewhere = scanner.Scan(new ScanOptions { All = true, Top = int.MaxValue })
                .Select(s => s.RealCwd)
                .OfType<string>()
                .Where(c => c.Length > 0);
            resolution = StandingTargets.Resolve(patterns, RootFolders().Concat(elsewhere));
        }

        if (resolution.Folders.Count == 0)
            throw new UsageException(
                $"Nothing matched {string.Join(", ", resolution.Unmatched.Select(u => $"'{u}'"))} — "
                + "not under the link roots, and no session ran in a folder by that name.");

        var entry = new ProjectStanding { Standing = standing, Note = note, At = DateTimeOffset.Now };
        var verb = args.Has("dry-run") ? "would mark" : "marked";
        var items = resolution.Folders.Select(f => new ActionItem
        {
            SessionId = "",
            Ok = true,
            Name = SessionCore.Standby.ProjectOf(f),
            Folder = f,
            Message = store.Of(f) == standing
                ? $"already {ProjectStanding.ToWire(standing)}" + (standing == Standing.Active ? "" : "; note refreshed")
                : $"{ProjectStanding.ToWire(store.Of(f))} → {ProjectStanding.ToWire(standing)}",
        }).ToList();
        items.AddRange(resolution.Unmatched.Select(u => new ActionItem
        {
            SessionId = "",
            Ok = false,
            Name = u,
            Message = "matched nothing",
        }));

        if (!args.Has("dry-run")) store.Set(resolution.Folders, entry);

        return Cli.EmitResult(new ActionResult
        {
            Ok = resolution.Unmatched.Count == 0,
            Action = "project",
            Message = $"{verb} {resolution.Folders.Count} project(s) {ProjectStanding.ToWire(standing)}"
                      + (note is null ? "." : $" — {note}."),
            Items = items,
            Warning = store.LoadWarning,
        });
    }

    /// <summary>Every folder directly under a link root: where projects live.</summary>
    private static IEnumerable<string> RootFolders()
    {
        foreach (var root in LinkRoots.Load().Roots)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in dirs) yield return d;
        }
    }
}
