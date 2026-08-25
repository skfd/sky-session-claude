using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SessionCore;

/// <summary>What happened to one rename, in a sentence fit for the status line.</summary>
public readonly record struct RenameResult(bool Ok, string Message)
{
    public static RenameResult Fail(string why) => new(false, why);
    public static RenameResult Done(string what) => new(true, what);
}

/// <summary>
/// Renames a live session where it stands, over the named pipe it publishes.
///
/// A restart is a kill and a resume, so it needs a terminal we can drive and a session with
/// nothing in flight. A rename touches only the name, so it reaches a session mid-turn and
/// costs nothing if it is refused — which is what makes it the one thing Sky may do unasked.
///
/// The protocol is two newline-delimited JSON objects: an auth line carrying the
/// <c>peerToken</c> out of the session's <c>&lt;pid&gt;.&lt;hash&gt;.key</c> file — the file is
/// a small JSON object, not the token itself — then the rename. A connection that does not
/// authenticate has its lines dropped and is closed, silently from this side, which is why
/// success is confirmed by reading the name back out of the registry rather than by the write
/// not throwing.
///
/// <b>Only <c>cli</c> sessions act on it.</b> docs/NAMING.md says otherwise, on the strength
/// of every live session publishing the pipe; publishing it and handling
/// <c>control/rename</c> turn out to be different things. Measured against this registry: a
/// <c>cli</c> session takes the rename and appends the <c>custom-title</c> to its transcript,
/// while <c>claude-desktop</c> and <c>sdk-cli</c> connect, accept the bytes, and do nothing —
/// no registry change and no transcript record. So the desktop and SDK sessions this was
/// meant to reach are named on the way back up like any other, and not in place.
/// </summary>
public static class SessionRenamer
{
    /// <summary>How long to wait for the pipe. It is local and already listening, or it is not there.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for the session to publish the new name. It writes its registry entry
    /// as it handles the message, so this is the round trip and not any real work.
    /// </summary>
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ConfirmPoll = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Rename <paramref name="live"/> to <paramref name="name"/>, and confirm it took.
    ///
    /// This is only the mechanism. Deciding the name is <see cref="NamePolicy"/>'s and
    /// recording it as Sky's is <see cref="SessionNaming.RenameAsync"/>'s — go through that
    /// rather than this, or the rename lands as a name indistinguishable from one the operator
    /// typed, which is the bug the design exists to fix.
    /// </summary>
    public static async Task<RenameResult> RenameAsync(LiveSession live, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RenameResult.Fail("no name to give it");

        if (live.MessagingSocketPath is not { Length: > 0 } socket)
            return RenameResult.Fail("it publishes no pipe to be spoken to on");

        if (LiveSessionRegistry.KeyPathFor(live.Pid) is not { } keyPath)
            return RenameResult.Fail($"no peer-token file beside its registry entry (pid {live.Pid})");

        string? token;
        try
        {
            token = PeerTokenIn(await File.ReadAllTextAsync(keyPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return RenameResult.Fail($"could not read its peer token: {e.Message}");
        }

        if (string.IsNullOrEmpty(token))
            return RenameResult.Fail($"its peer-token file carries no token ({Path.GetFileName(keyPath)})");

        try
        {
            await SendAsync(socket, token, name);
        }
        catch (TimeoutException)
        {
            return RenameResult.Fail("it did not answer its pipe — it may be shutting down");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return RenameResult.Fail($"could not reach it over its pipe: {e.Message}");
        }

        // The write succeeding proves only that the bytes left; a connection that failed to
        // authenticate is dropped without a word. The registry is the only thing that can say
        // the session actually took the name.
        if (await Confirmed(live, name)) return RenameResult.Done($"renamed to \"{name}\"");

        return RenameResult.Fail(
            $"the rename was sent but it still answers to \"{live.Name}\"{Because(live)}");
    }

    /// <summary>
    /// The token out of a <c>.key</c> file, which is a small JSON object rather than the token
    /// itself: <c>{"peerToken":"…","procStartFt":"…"}</c>. Sending the whole file as the token
    /// authenticates as nobody, and an unauthenticated connection is dropped in silence — so
    /// getting this wrong looks exactly like the rename simply not arriving.
    /// </summary>
    public static string? PeerTokenIn(string keyFileText)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyFileText);
            return doc.RootElement.TryGetProperty("peerToken", out var el)
                && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // Not JSON: an older or newer layout holding the bare token. Worth trying, since
            // the alternative is refusing a session we could have renamed.
            var text = keyFileText.Trim();
            return text.Length > 0 ? text : null;
        }
    }

    /// <summary>
    /// The likely reason a rename went unanswered, when we know one. Checked after the fact
    /// rather than refused up front: a build that starts honouring these should quietly begin
    /// working rather than keep being turned away by a rule written today.
    /// </summary>
    private static string Because(LiveSession live) =>
        string.Equals(live.Entrypoint, "cli", StringComparison.OrdinalIgnoreCase) || live.Entrypoint.Length == 0
            ? ""
            : $" — it runs under {live.Entrypoint}, which publishes a pipe but does not act on a rename";

    private static async Task SendAsync(string socketPath, string token, string name)
    {
        using var pipe = new NamedPipeClientStream(
            ".", PipeNameOf(socketPath), PipeDirection.Out, PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(ConnectTimeout);
        await pipe.ConnectAsync(cts.Token);

        // Newline-delimited JSON, auth first. Serialized rather than interpolated: a name is
        // arbitrary text, and a quote in one would otherwise be a malformed message the
        // session drops without saying why.
        await WriteLine(pipe, new { type = "auth", token });
        await WriteLine(pipe, new { type = "control", action = "rename", name });
        await pipe.FlushAsync();

        // Disposing the client can tear the pipe down before the session has read what is
        // still sitting in it, which would look identical to a rename that was ignored.
        // SessionCore targets plain net10.0 even though every consumer is Windows, so the
        // guard is for the analyzer rather than for any platform this runs on.
        if (OperatingSystem.IsWindows()) pipe.WaitForPipeDrain();
    }

    private static async Task WriteLine(Stream stream, object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        await stream.WriteAsync(bytes);
    }

    /// <summary>
    /// The pipe name as <see cref="NamedPipeClientStream"/> wants it: what follows
    /// <c>\\.\pipe\</c>. The registry publishes a full path, and the server half of it is
    /// always this machine — a session on another machine is not one we could rename.
    /// </summary>
    public static string PipeNameOf(string socketPath)
    {
        var text = socketPath.Replace('/', '\\');
        const string marker = @"\pipe\";

        int cut = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return cut >= 0 ? text[(cut + marker.Length)..] : text;
    }

    /// <summary>The registry reporting the new name back, which is the only proof there is.</summary>
    private static async Task<bool> Confirmed(LiveSession live, string name)
    {
        var until = DateTime.UtcNow + ConfirmTimeout;
        while (true)
        {
            // The session's own entry, by pid: a rename does not restart anything, so the
            // process this was sent to is the process that answers for it.
            var current = await Task.Run(() => LiveSessionRegistry.ReadOne(live.Pid));
            if (current is not null
                && string.Equals(current.SessionId, live.SessionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Name, name, StringComparison.Ordinal))
                return true;

            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(ConfirmPoll);
        }
    }
}
