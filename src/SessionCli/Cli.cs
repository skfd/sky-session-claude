using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionCli;

/// <summary>Raised for anything the caller can fix by typing something else.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Parsed command line: the verb, its positional arguments, and its flags.
///
/// Flags are matched case-insensitively and may be spelled <c>--flag value</c> or
/// <c>--flag=value</c>; an agent writing these by hand should not have to guess which.
/// </summary>
internal sealed class Args
{
    /// <summary>
    /// Flags that are on-or-off and never take a value.
    ///
    /// Without this list the parser cannot tell <c>done --dry-run abc</c> from
    /// <c>list --status complete</c>: both are a flag followed by a bare word, and guessing
    /// costs you the session id — the command then reports it has nothing to do while
    /// looking like it worked. Naming the switches is the only way to read both correctly.
    /// </summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest-per-project", "unfinished", "live", "stale",
        "tip", "resume", "yes", "force", "dry-run", "self", "ask",
    };

    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    public string Verb { get; }
    public IReadOnlyList<string> Positional => _positional;

    public Args(string verb, IEnumerable<string> rest)
    {
        Verb = verb;

        var args = rest.ToList();
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--"))
            {
                _positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (name.Split('=', 2) is [var lhs, var rhs])
            {
                _flags[lhs] = rhs;
                continue;
            }

            if (Switches.Contains(name))
            {
                _flags[name] = null;
                continue;
            }

            // A value belongs to this flag only if it does not look like the next flag;
            // a value-taking flag left bare is caught later, by Require or Int.
            var next = i + 1 < args.Count ? args[i + 1] : null;
            if (next is not null && !next.StartsWith("--"))
            {
                _flags[name] = next;
                i++;
            }
            else
            {
                _flags[name] = null;
            }
        }
    }

    public bool Has(string name) => _flags.ContainsKey(name);

    public string? Value(string name) => _flags.TryGetValue(name, out var v) ? v : null;

    public string Require(string name) =>
        Value(name) ?? throw new UsageException($"--{name} requires a value.");

    public int Int(string name, int fallback)
    {
        if (!_flags.TryGetValue(name, out var raw)) return fallback;
        if (raw is null) throw new UsageException($"--{name} requires a value.");
        return int.TryParse(raw, out var n)
            ? n
            : throw new UsageException($"--{name} expects an integer, got '{raw}'.");
    }

    /// <summary>Reject a flag this verb does not understand, rather than ignoring it.</summary>
    public void RejectUnknown(params string[] known)
    {
        var unknown = _flags.Keys.FirstOrDefault(k =>
            !known.Contains(k, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new UsageException($"'{Verb}' does not take --{unknown}.");
    }
}

/// <summary>
/// Entry point: works out which verb was asked for and hands off to <see cref="Commands"/>.
///
/// The bare invocation and the old flag-only forms are still exactly what they were — the
/// morning brief runs <c>SessionCli --json &lt;path&gt;</c> on a schedule and must not
/// notice that verbs happened.
/// </summary>
internal static class Cli
{
    public const string HelpText =
        """
        SessionCli - read and drive Claude Code sessions from a script or an agent.

        Reading
          SessionCli                          every session as JSON (same as `list`)
          SessionCli list [filters]           the session list, filtered
          SessionCli show <id>                one session in full, with its fork points
          SessionCli live                     only the sessions open in a terminal now
          SessionCli peek <id>                what a live session's terminal shows now

        Marking            (yours, not the classifier's; the app sees these within seconds)
          SessionCli done <id>...             tick it off, whatever the file ended on
          SessionCli undone <id>...           clear that tick
          SessionCli abandon <id>...          cross it out; stays honestly unfinished
          SessionCli restore <id>...          clear that cross

        Acting
          SessionCli fork <id> --at-prompt <n>   branch from before prompt n (no terminal)
          SessionCli fork <id> --tip             branch at the tip, in a new terminal
          SessionCli rename <id> [name]          rename it where it stands; no name = we pick
          SessionCli rename --self [name]        rename the session this is running in
          SessionCli rename <id> --ask           pay a small model to read it and name it
          SessionCli restart <id>...             restart in the terminal it already sits in
          SessionCli restart --stale             restart every stale session that is idle
          SessionCli resume <id>                 open a terminal and resume it
          SessionCli new [--in <path>]           open a terminal on a brand-new session
          SessionCli trust <id>                  answer the trust prompt it is sitting on

        Filters for `list`
          --status <s>          complete, waiting-you, waiting-agent, cut-off, limit,
                                error, interrupted
          --project <name>      substring match on the project
          --search <text>       substring match on name, prompt or recap
          --disposition <d>     none, done or abandoned
          --unfinished          drop everything settled (complete, or marked done)
          --live                only sessions open in a terminal right now
          --stale               only live sessions behind the installed build
          --top <n>             cap how many session files are scanned (default: all)
          --limit <n>           cap how many rows come back after filtering
          --newest-per-project  one session per project
          --context-window <n>  token budget for Ctx% (default 200000)
          --json <path>         write to a file instead of stdout

        Flags for the acting verbs
          --in <path>  folder `new` starts in (default: the folder you are in)
          --name <n>   what a new session answers to (default: the CLI derives one)
          --trust      on `new`: wait for the trust prompt in that folder and accept it
          --yes        actually do it; `restart --stale` only reports the plan without it
          --force      act on the session this command is running inside (refused otherwise)
          --self       on `rename`: the session this command is running in
          --ask        on `rename`: read the session with `claude -p` when nothing free
                       can name it (~2c and ~5s; refused when a free source would do)
          --dry-run    say what would happen and change nothing

        A session id may be shortened to any unique prefix, like a commit sha.
        """;

    public static int Run(string[] args)
    {
        // The output is UTF-8 JSON whether it goes to a file or a pipe; without this the
        // console re-encodes it to the OEM code page and every em dash arrives as a "?".
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch (IOException) { /* no console to configure; the pipe is already bytes */ }

        try
        {
            // No verb, or straight into flags: the pre-verb command line, unchanged.
            if (args.Length == 0 || args[0].StartsWith("--"))
                return Commands.List(new Args("list", args));

            var verb = args[0].ToLowerInvariant();
            var rest = new Args(verb, args.Skip(1));

            return verb switch
            {
                "list" => Commands.List(rest),
                "show" => Commands.Show(rest),
                "live" => Commands.Live(rest),
                "peek" => Commands.Peek(rest),
                "done" => Commands.Mark(rest, SessionCore.Disposition.Done),
                "undone" => Commands.Mark(rest, SessionCore.Disposition.None),
                "abandon" => Commands.Mark(rest, SessionCore.Disposition.Abandoned),
                "restore" => Commands.Mark(rest, SessionCore.Disposition.None),
                "fork" => Commands.Fork(rest),
                "rename" => Commands.Rename(rest),
                "restart" => Commands.Restart(rest),
                "resume" => Commands.Resume(rest),
                "new" => Commands.New(rest),
                "trust" => Commands.Trust(rest),
                "help" or "-h" or "-?" => Print(HelpText),
                _ => throw new UsageException($"Unknown command: {args[0]}"),
            };
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(HelpText);
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }

    // --- JSON output --------------------------------------------------------

    // Nulls are written, not dropped: the morning brief has read this shape since it was a
    // PowerShell script, and a machine reader is better served by an explicit null than by
    // a field that silently is not there.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Write JSON to stdout, or to a file when the caller asked for one.</summary>
    public static void Emit<T>(T value, string? path = null)
    {
        var json = ToJson(value);
        if (path is null)
        {
            Console.WriteLine(json);
            return;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
    }

    /// <summary>Emit an action's result and turn it into the process's exit code.</summary>
    public static int EmitResult(ActionResult result)
    {
        Emit(result);
        return result.Ok ? 0 : 1;
    }
}
