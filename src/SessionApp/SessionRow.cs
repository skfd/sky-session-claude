using SessionCore;

namespace SessionApp;

/// <summary>
/// Thin display wrapper over a <see cref="SessionInfo"/>. Keeps view-specific
/// formatting (two-line timestamp, "Ctx%" string) out of the core model while
/// exposing the raw fields the grid and the resume action need.
/// </summary>
public sealed class SessionRow
{
    public SessionRow(SessionInfo info) => Info = info;

    public SessionInfo Info { get; }

    // Two lines: absolute timestamp over a relative "how long ago".
    public string LastActiveDisplay =>
        $"{Info.LastActive:yyyy-MM-dd HH:mm}\n{TextUtil.RelativeAge(Info.LastActive)}";

    public DateTime LastActive => Info.LastActive;
    public string Name => Info.Name ?? "(untitled)";
    public string Project => Info.Project;
    public string Status => Info.Status.ToWire();
    public bool Complete => Info.Complete;
    public string CtxDisplay => Info.ContextPct is int p ? $"{p}%" : "";
    public string LastPrompt => Info.LastPrompt;
    public string Recap => Info.Recap;
    public double SizeKB => Info.SizeKB;
    public string Command => Info.Command;
}
