using System.Text.Json;

namespace SessionCore;

/// <summary>
/// Reads the one piece of ~/.claude/settings.json the scanner needs: whether the
/// operator's default model requests the extended 1M context window. Session
/// transcripts record the bare model id (e.g. "claude-fable-5") with the "[1m]"
/// suffix stripped, so below the 200k token threshold the configured default is
/// the only positive signal that a session ran with the 1M window.
/// </summary>
public static class ClaudeSettings
{
    public const string LargeModelSuffix = "[1m]";

    public static string DefaultSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>
    /// The base model id (suffix stripped) when the configured "model" carries the
    /// "[1m]" suffix; null when no 1M model is configured or settings are unreadable.
    /// </summary>
    public static string? ReadLargeModelId(string? settingsPath = null)
    {
        try
        {
            var path = settingsPath ?? DefaultSettingsPath();
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("model", out var m)
                || m.ValueKind != JsonValueKind.String) return null;
            var model = m.GetString() ?? "";
            return model.EndsWith(LargeModelSuffix, StringComparison.Ordinal)
                ? model[..^LargeModelSuffix.Length]
                : null;
        }
        catch
        {
            return null;
        }
    }
}
