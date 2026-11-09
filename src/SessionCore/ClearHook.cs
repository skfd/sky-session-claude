using System.Text.Json;

namespace SessionCore;

/// <summary>
/// What a <c>SessionStart</c> hook tells a session that a <c>/clear</c> just opened.
///
/// <c>/clear</c> starts a new session id in the same terminal and copies the old name across.
/// Whether that name still fits depends on what the operator asks next. <c>/clear</c> is as
/// often "same task, fresh context" as it is "something else now", so nothing outside the
/// conversation can tell which. The hook therefore renames nothing. It tells the agent the
/// name was inherited and leaves the decision to the one party that will know.
///
/// The old conversation's declared state needs no word here: the fold reads a cleared
/// conversation as settled from its own transcript (see
/// <see cref="SessionFileFields.EndedByClear"/>), and this new id has declared nothing yet.
/// </summary>
public static class ClearHook
{
    /// <summary>
    /// The line handed to the agent, or null when the hook should stay silent. That covers
    /// every session start but a <c>/clear</c>, so the hook is harmless installed without a
    /// matcher.
    /// </summary>
    public static string? ContextFor(string? source) =>
        string.Equals(source, "clear", StringComparison.OrdinalIgnoreCase)
            ? "This conversation was started by /clear. The session name it shows was copied over "
              + "from the conversation before it, and anything that conversation declared with "
              + "SessionCli state belonged to that one, not this one. Once the operator's first "
              + "prompt shows what this conversation is about, rename the session if its subject "
              + "changed, and keep the name if it did not."
            : null;

    /// <summary>
    /// The hook's whole answer for the JSON Claude Code hands it on stdin: the
    /// <c>hookSpecificOutput</c> to print, or null to print nothing. Never throws. A hook that
    /// fails on a malformed payload has nothing useful to say, so it says nothing.
    /// </summary>
    public static string? Answer(string? stdin)
    {
        if (string.IsNullOrWhiteSpace(stdin)) return null;

        string? source;
        try
        {
            using var doc = JsonDocument.Parse(stdin);
            source = doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("source", out var s)
                     && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (ContextFor(source) is not { } context) return null;

        return JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "SessionStart",
                additionalContext = context,
            },
        });
    }
}
