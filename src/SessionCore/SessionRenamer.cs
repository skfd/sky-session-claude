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
/// This is the reason renaming reaches further than restarting does. A restart is a kill and
/// a resume, so it needs a terminal we can drive and a session with nothing in flight; a
/// rename touches only the name, so it works on the desktop app, on the SDK, and on a session
/// mid-turn. Nothing can be lost, which is what makes it the one thing Sky may do unasked.
///
/// The protocol is two newline-delimited JSON objects: an auth line carrying the peer token
/// from the session's <c>&lt;pid&gt;.&lt;hash&gt;.key</c> file, then the rename itself. A
/// connection that does not authenticate has its lines dropped and is closed — silently, from
/// this side, which is why success is confirmed by reading the name back out of the registry
/// rather than by the write not throwing.
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
    /// Recording the name as Sky's is the caller's job, and has to happen whether or not this
    /// returns Ok: a rename that landed and was not recorded is exactly the masquerade this
    /// design exists to fix.
    /// </summary>
    public static async Task<RenameResult> RenameAsync(LiveSession live, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RenameResult.Fail("no name to give it");

        if (live.MessagingSocketPath is not { Length: > 0 } socket)
            return RenameResult.Fail("it publishes no pipe to be spoken to on");

        if (LiveSessionRegistry.KeyPathFor(live.Pid) is not { } keyPath)
            return RenameResult.Fail($"no peer-token file beside its registry entry (pid {live.Pid})");

        string token;
        try
        {
            token = (await File.ReadAllTextAsync(keyPath)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return RenameResult.Fail($"could not read its peer token: {e.Message}");
        }

        if (token.Length == 0)
            return RenameResult.Fail("its peer-token file is empty");

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
        return await Confirmed(live, name)
            ? RenameResult.Done($"renamed to \"{name}\"")
            : RenameResult.Fail($"the rename was sent but it still answers to \"{live.Name}\"");
    }

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
            var current = await Task.Run(() => LiveSessions.Find(live.SessionId));
            if (current is not null && string.Equals(current.Name, name, StringComparison.Ordinal))
                return true;

            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(ConfirmPoll);
        }
    }
}
