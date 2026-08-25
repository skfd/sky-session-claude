namespace SessionCore;

/// <summary>
/// How willing we are to quit a session on its behalf — restarting it or closing it for
/// the night both end the same way, so both weigh their risk on this one scale.
/// </summary>
public enum SweepSafety
{
    /// <summary>Nothing is in flight and nothing is waiting on you — go ahead.</summary>
    Safe,

    /// <summary>Probably fine, but something could be lost. Offer it; never sweep it up.</summary>
    Ask,

    /// <summary>Don't, and say why.</summary>
    Unsafe,
}

/// <summary>A verdict plus the sentence shown on the card or in the skipped-count.</summary>
public readonly record struct SweepVerdict(SweepSafety Safety, string Reason)
{
    public bool CanSweep => Safety == SweepSafety.Safe;
}

/// <summary>
/// Decides which live sessions may be restarted unattended.
///
/// A restart is a kill and a resume. The conversation itself is never at risk — it is on
/// disk, and <c>--resume</c> replays it — so the whole question is what lives only inside
/// the running process: a turn in flight, a reply you have half-typed, background work
/// the session owns.
///
/// The single unfixable gap is the input box. Text you typed but did not send exists in
/// no file and in no registry field, so nothing outside the process can see it. That is
/// why a session that asked you a question is only ever offered, never swept: the sweep
/// has to be the set where we can prove nothing is lost, not the set that looks quiet.
/// </summary>
public static class RestartPolicy
{
    /// <summary>
    /// How long a session must have been idle before "idle" is trusted. A session that
    /// went idle two seconds ago is one you are most likely still sitting in front of.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Judge one live session. <paramref name="tail"/> is the status the scanner read from
    /// the session file (null when the file was not scanned), and <paramref name="now"/> is
    /// passed in so the rule is testable rather than clock-dependent.
    /// </summary>
    public static SweepVerdict Judge(LiveSession live, SessionStatus? tail, DateTime now)
    {
        // --- can we drive it at all? -----------------------------------------
        if (!string.Equals(live.Kind, "interactive", StringComparison.OrdinalIgnoreCase))
            return new(SweepSafety.Unsafe, "not an interactive session");

        if (!string.Equals(live.Entrypoint, "cli", StringComparison.OrdinalIgnoreCase))
            return new(SweepSafety.Unsafe, $"runs under {Host(live.Entrypoint)}, not a terminal we can drive");

        // --- is something in flight? -----------------------------------------
        if (live.Status is null)
            return new(SweepSafety.Unsafe, "publishes no busy/idle state, so we cannot tell if it is mid-turn");

        if (string.Equals(live.Status, "busy", StringComparison.OrdinalIgnoreCase))
            return new(SweepSafety.Unsafe, "a turn is in flight");

        // "waiting" is its own state, distinct from idle: the CLI is blocked on an answer
        // from you — a permission prompt, or a question mid-turn. Nothing is running, so a
        // restart works, but it drops whatever was waiting to be approved.
        if (string.Equals(live.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            return new(SweepSafety.Ask, "it is waiting on an answer from you — a pending approval would be dropped");

        if (!string.Equals(live.Status, "idle", StringComparison.OrdinalIgnoreCase))
            return new(SweepSafety.Unsafe, $"unrecognised state \"{live.Status}\"");

        // The file's own tail can contradict "idle": a tool step that never came back
        // leaves the process at rest with work half-done, and killing it is a judgment
        // call about abandoning that work — yours, not ours.
        if (tail is SessionStatus.WaitingAgent or SessionStatus.CutOff)
            return new(SweepSafety.Unsafe, $"stopped mid-step ({tail.Value.ToWire()})");

        // --- could a restart still cost something? ---------------------------
        if (live.StatusUpdatedAt is { } since && now - since < SettleTime)
            return new(SweepSafety.Ask, "only just went idle — you may still be typing in it");

        if (tail is SessionStatus.WaitingYou)
            return new(SweepSafety.Ask, "it asked you something; a reply you have typed but not sent would be lost");

        return new(SweepSafety.Safe, "idle and settled");
    }

    /// <summary>
    /// The command that brings this session back. Remote Control is opt-in per session and
    /// does not survive the restart, so a session that had it connected asks for it again
    /// on the way back up — otherwise restarting would quietly drop it off your phone.
    ///
    /// <paramref name="name"/> is what the session comes back under, decided elsewhere. This
    /// used to work it out here — carry a chosen name over, re-derive anything else — and that
    /// was the gap: a restart re-froze whatever placeholder Sky had written last time, so
    /// recording provenance bought nothing. Deciding is <see cref="NamePolicy"/>'s and arrives
    /// here as a parameter, which keeps this function what it was: pure, and testable without
    /// a store to set up.
    ///
    /// The name goes in under <c>--name</c>, never as the optional argument to
    /// <c>--remote-control</c>. The two are separate flags inside the CLI and only
    /// <c>--name</c> reaches the registry: a name passed to <c>--remote-control</c> is
    /// accepted, ignored, and the session comes back derived anyway.
    ///
    /// <c>--name</c> does reach the transcript, as a <c>custom-title</c> record. The comment
    /// that used to sit here said the opposite and was wrong — which is exactly why the name
    /// passed in has to be one the policy chose: whatever goes in here is read back as this
    /// session's title next time.
    /// </summary>
    public static string ResumeCommand(LiveSession live, string? name = null)
    {
        var chosen = name is { Length: > 0 } ? name : SessionName.Floor(live.SessionId, live.Cwd);

        var command = $"claude --resume {live.SessionId} --name {SessionName.Quote(chosen)}";
        return live.RemoteControl ? $"{command} --remote-control" : command;
    }

    /// <summary>
    /// The full line typed into the terminal the session vacated: back to its folder first,
    /// because the shell may have been left somewhere else before Claude was started.
    /// </summary>
    public static string RelaunchLine(LiveSession live, string? name = null) =>
        live.Cwd is { Length: > 0 } cwd
            ? $"cd {SessionName.Quote(cwd)}; {ResumeCommand(live, name)}"
            : ResumeCommand(live, name);


    private static string Host(string entrypoint) => entrypoint switch
    {
        "claude-desktop" => "the desktop app",
        "sdk-cli" => "the SDK",
        "" => "an unknown host",
        _ => entrypoint,
    };
}
