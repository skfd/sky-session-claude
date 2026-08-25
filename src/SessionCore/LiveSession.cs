using System.Text.Json;

namespace SessionCore;

/// <summary>
/// One entry of the live-session registry: a Claude CLI that is running right now.
///
/// Every running interactive CLI publishes <c>~/.claude/sessions/&lt;pid&gt;.json</c>
/// while it lives. It is the only reliable map from a session id to a process (the id
/// is generated at runtime, so it appears on no command line and in no environment
/// block), and it carries the three facts a restart needs: which build the session is
/// running, whether it is mid-turn, and whether Remote Control is connected.
/// </summary>
public sealed record LiveSession
{
    public int Pid { get; init; }
    public string SessionId { get; init; } = "";
    public string? Cwd { get; init; }

    /// <summary>The CLI build this session started under — the point of the whole feature.</summary>
    public string? Version { get; init; }

    /// <summary>"busy" while a turn is in flight, "idle" at the prompt, null on builds that publish neither.</summary>
    public string? Status { get; init; }

    public DateTime? StatusUpdatedAt { get; init; }

    /// <summary>
    /// Set once Remote Control has connected this session to the account. Remote Control
    /// is opt-in per session (<c>/remote-control</c>, or <c>--remote-control</c> at launch)
    /// and does not survive a restart, so a restart that does not re-request it silently
    /// drops the session off your phone.
    /// </summary>
    public string? BridgeSessionId { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// Why the session answers to <see cref="Name"/>: "derived" when the CLI slugged it from
    /// the folder, "collision" when it yielded a claimed name for a suffixed one. A name you
    /// supplied is written with this field absent, so null means chosen — see
    /// <see cref="SessionName.IsChosen"/>.
    /// </summary>
    public string? NameSource { get; init; }

    /// <summary>
    /// The named pipe this session listens on, e.g. <c>\\.\pipe\cc-msg-6abe4d3f…</c>. Null on
    /// builds that publish none.
    ///
    /// It is what lets a session be renamed where it stands rather than restarted, which is
    /// why rename reaches sessions restart cannot: the desktop app and the SDK publish this
    /// too, and neither runs in a terminal we could drive. Authentication is the sibling
    /// <c>&lt;pid&gt;.&lt;hash&gt;.key</c> file — see <see cref="LiveSessionRegistry.KeyPathFor"/>.
    /// </summary>
    public string? MessagingSocketPath { get; init; }

    public string Kind { get; init; } = "";
    public string Entrypoint { get; init; } = "";

    /// <summary>True when Remote Control is connected for this session.</summary>
    public bool RemoteControl => !string.IsNullOrEmpty(BridgeSessionId);

    /// <summary>True when this session can be renamed in place.</summary>
    public bool CanRename => !string.IsNullOrEmpty(MessagingSocketPath);
}

/// <summary>Reads the live-session registry directory into <see cref="LiveSession"/> records.</summary>
public static class LiveSessionRegistry
{
    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    /// <summary>
    /// One process's registry entry, read directly rather than by scanning the directory.
    ///
    /// A rename keeps the pid, so confirming one is a question about a single known file.
    /// Asking it through a full scan means enumerating every entry and looking up every
    /// process, twenty-five times over a five-second poll — per session, on a pass that walks
    /// them all.
    /// </summary>
    public static LiveSession? ReadOne(int pid, string? dir = null)
    {
        try
        {
            var path = Path.Combine(dir ?? DefaultDir(), $"{pid}.json");
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;   // racing, partial, or gone — the caller polls again
        }
    }

    /// <summary>
    /// The peer-token file for a session, sitting beside its registry entry as
    /// <c>&lt;pid&gt;.&lt;hash&gt;.key</c>. Null when there is none, which is the same thing as
    /// "this session cannot be spoken to".
    ///
    /// Found by globbing on the pid, because only the pid half of the name is knowable from
    /// the registry — the hash is the session's own and appears nowhere else.
    /// </summary>
    public static string? KeyPathFor(int pid, string? dir = null)
    {
        dir ??= DefaultDir();
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, $"{pid}.*.key").FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every readable registry entry in <paramref name="dir"/>. Entries can be mid-write
    /// or left behind by a dead process; unreadable ones are skipped rather than thrown,
    /// and liveness is the caller's job (the pid may have exited, or been reused).
    /// </summary>
    public static IReadOnlyList<LiveSession> Read(string dir)
    {
        var found = new List<LiveSession>();
        if (!Directory.Exists(dir)) return found;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (Parse(File.ReadAllText(path)) is { } session) found.Add(session);
            }
            catch { /* racing, partial, or not ours — skip it */ }
        }
        return found;
    }

    /// <summary>
    /// One registry file's JSON, or null when it names no session or no process.
    /// Unknown fields are ignored, so a future CLI can add them without breaking us.
    /// </summary>
    public static LiveSession? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (Str(root, "sessionId") is not { Length: > 0 } sessionId) return null;
        if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid)) return null;

        return new LiveSession
        {
            Pid = pid,
            SessionId = sessionId,
            Cwd = Str(root, "cwd"),
            Version = Str(root, "version"),
            Status = Str(root, "status"),
            StatusUpdatedAt = UnixMs(root, "statusUpdatedAt"),
            BridgeSessionId = Str(root, "bridgeSessionId"),
            Name = Str(root, "name"),
            NameSource = Str(root, "nameSource"),
            MessagingSocketPath = Str(root, "messagingSocketPath"),
            Kind = Str(root, "kind") ?? "",
            Entrypoint = Str(root, "entrypoint") ?? "",
        };
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static DateTime? UnixMs(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
            : null;
}
