using System.Text.Json;
using System.Text.Json.Serialization;
using SessionCore;

// Headless counterpart to the WPF app: scans ~/.claude/projects and emits the
// session list as JSON. Shares SessionCore with the app so both agree on how a
// session's status is classified (no second implementation to drift).
//
// Usage:
//   SessionCli                       JSON to stdout
//   SessionCli --json <path>         JSON to a file (dirs created); confirmation on stderr
//   SessionCli --top <n>             cap how many sessions (default 50)
//   SessionCli --newest-per-project  one session per project instead of all
//   SessionCli --context-window <n>  token budget for Ctx% (default 200000)
//
// The morning brief runs in a sandbox that cannot read ~/.claude/projects (a
// protected location), so a scheduled task on the host runs `--json <path>` to a
// file the sandbox can read. The JSON shape matches get-claudesessions.ps1 -Json.

const string HelpText =
    """
    SessionCli - headless Claude Code session scanner (JSON output).

      SessionCli                       JSON to stdout
      SessionCli --json <path>         JSON to a file (dirs created)
      SessionCli --top <n>             cap sessions (default 50)
      SessionCli --newest-per-project  one session per project (default: all)
      SessionCli --context-window <n>  token budget for Ctx% (default 200000)
    """;

string? jsonPath = null;
int top = 50;
bool all = true;
int contextWindow = SessionFileParser.DefaultContextWindow;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            jsonPath = RequireValue(args, ref i);
            break;
        case "--top":
            top = ParseInt(RequireValue(args, ref i), "--top");
            break;
        case "--newest-per-project":
            all = false;
            break;
        case "--context-window":
            contextWindow = ParseInt(RequireValue(args, ref i), "--context-window");
            break;
        case "-h" or "--help":
            Console.WriteLine(HelpText);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine(HelpText);
            return 2;
    }
}

var scanner = new SessionScanner();
if (!scanner.ProjectsDirExists)
{
    Console.Error.WriteLine($"No Claude Code projects folder found at: {scanner.ProjectsDir}");
    return 1;
}

var sessions = scanner
    .Scan(new ScanOptions { All = all, Top = top, ContextWindow = contextWindow })
    .Select(SessionDto.From)
    .ToList();

var payload = new ExportDto
{
    GeneratedAt = DateTimeOffset.Now.ToString("o"),
    Count = sessions.Count,
    Sessions = sessions,
};

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

if (jsonPath is null)
{
    Console.WriteLine(JsonSerializer.Serialize(payload, options));
    return 0;
}

var dir = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, options), new System.Text.UTF8Encoding(false));
Console.Error.WriteLine($"Wrote {sessions.Count} session(s) to {jsonPath}");
return 0;

static string RequireValue(string[] args, ref int i)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException($"{args[i]} requires a value.");
    return args[++i];
}

static int ParseInt(string value, string flag) =>
    int.TryParse(value, out var n) ? n : throw new ArgumentException($"{flag} expects an integer, got '{value}'.");

// --- DTOs: the JSON contract the morning brief consumes -----------------------
// Field names and order mirror get-claudesessions.ps1 -Json so the brief needs
// no changes when it switches to this binary.

sealed class ExportDto
{
    public required string GeneratedAt { get; init; }
    public required int Count { get; init; }
    public required List<SessionDto> Sessions { get; init; }
}

sealed class SessionDto
{
    public required DateTime LastActive { get; init; }
    public required double AgeDays { get; init; }
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required string Status { get; init; }
    public required bool Complete { get; init; }
    public required int? ContextPct { get; init; }
    public required int ContextTokens { get; init; }
    public required string LastPrompt { get; init; }
    public required string Recap { get; init; }
    public required bool Unfinished { get; init; }
    public required string WaitingOn { get; init; }
    public required string Cwd { get; init; }
    public required string SessionId { get; init; }
    public required double SizeKB { get; init; }
    public required string Command { get; init; }

    public static SessionDto From(SessionInfo s) => new()
    {
        LastActive = s.LastActive,
        AgeDays = s.AgeDays,
        Name = s.Name ?? "(untitled)",
        Project = s.Project,
        Status = s.Status.ToWire(),
        Complete = s.Complete,
        ContextPct = s.ContextPct,
        ContextTokens = s.ContextTokens,
        LastPrompt = s.LastPrompt,
        Recap = s.Recap,
        Unfinished = s.Unfinished,
        WaitingOn = s.WaitingOn,
        Cwd = s.Cwd ?? "",
        SessionId = s.SessionId,
        SizeKB = s.SizeKB,
        Command = s.Command,
    };
}
