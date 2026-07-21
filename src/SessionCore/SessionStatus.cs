namespace SessionCore;

/// <summary>
/// End-state of a session file, mirroring the 7-state classifier in the original
/// get-claudesessions.ps1. The string values match what the PowerShell script
/// emitted so existing consumers (JSON export, morning brief) stay compatible.
/// </summary>
public enum SessionStatus
{
    Complete,
    WaitingYou,
    WaitingAgent,
    CutOff,
    Limit,
    Error,
    Interrupted,
}

public static class SessionStatusExtensions
{
    /// <summary>The wire string used by the original script (e.g. "waiting-you").</summary>
    public static string ToWire(this SessionStatus status) => status switch
    {
        SessionStatus.Complete => "complete",
        SessionStatus.WaitingYou => "waiting-you",
        SessionStatus.WaitingAgent => "waiting-agent",
        SessionStatus.CutOff => "cut-off",
        SessionStatus.Limit => "limit",
        SessionStatus.Error => "error",
        SessionStatus.Interrupted => "interrupted",
        _ => "complete",
    };
}
