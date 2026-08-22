namespace SessionCore;

/// <summary>How willing we are to restart a session on its behalf.</summary>
public enum RestartSafety
{
    /// <summary>Nothing is in flight and nothing is waiting on you — restart it.</summary>
    Safe,

    /// <summary>Probably fine, but something could be lost. Offer it; never sweep it up.</summary>
    Ask,

    /// <summary>Don't, and say why.</summary>
    Unsafe,
}

/// <summary>A verdict plus the sentence shown on the card or in the skipped-count.</summary>
public readonly record struct RestartVerdict(RestartSafety Safety, string Reason)
{
    public bool CanSweep => Safety == RestartSafety.Safe;
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
    public static RestartVerdict Judge(LiveSession live, SessionStatus? tail, DateTime now)
    {
        // --- can we drive it at all? -----------------------------------------
        if (!string.Equals(live.Kind, "interactive", StringComparison.OrdinalIgnoreCase))
            return new(RestartSafety.Unsafe, "not an interactive session");

        if (!string.Equals(live.Entrypoint, "cli", StringComparison.OrdinalIgnoreCase))
            return new(RestartSafety.Unsafe, $"runs under {Host(live.Entrypoint)}, not a terminal we can drive");

        // --- is something in flight? -----------------------------------------
        if (live.Status is null)
            return new(RestartSafety.Unsafe, "publishes no busy/idle state, so we cannot tell if it is mid-turn");

        if (string.Equals(live.Status, "busy", StringComparison.OrdinalIgnoreCase))
            return new(RestartSafety.Unsafe, "a turn is in flight");

        // "waiting" is its own state, distinct from idle: the CLI is blocked on an answer
        // from you — a permission prompt, or a question mid-turn. Nothing is running, so a
        // restart works, but it drops whatever was waiting to be approved.
        if (string.Equals(live.Status, "waiting", StringComparison.OrdinalIgnoreCase))
            return new(RestartSafety.Ask, "it is waiting on an answer from you — a pending approval would be dropped");

        if (!string.Equals(live.Status, "idle", StringComparison.OrdinalIgnoreCase))
            return new(RestartSafety.Unsafe, $"unrecognised state \"{live.Status}\"");

        // The file's own tail can contradict "idle": a tool step that never came back
        // leaves the process at rest with work half-done, and killing it is a judgment
        // call about abandoning that work — yours, not ours.
        if (tail is SessionStatus.WaitingAgent or SessionStatus.CutOff)
            return new(RestartSafety.Unsafe, $"stopped mid-step ({tail.Value.ToWire()})");

        // --- could a restart still cost something? ---------------------------
        if (live.StatusUpdatedAt is { } since && now - since < SettleTime)
            return new(RestartSafety.Ask, "only just went idle — you may still be typing in it");

        if (tail is SessionStatus.WaitingYou)
            return new(RestartSafety.Ask, "it asked you something; a reply you have typed but not sent would be lost");

        return new(RestartSafety.Safe, "idle and settled");
    }

    /// <summary>
    /// The command that brings this session back. Remote Control is opt-in per session and
    /// does not survive the restart, so a session that had it connected asks for it again
    /// on the way back up — otherwise restarting would quietly drop it off your phone.
    ///
    /// A name you chose is carried over untouched. One the CLI invented is not: it would be
    /// re-derived with a fresh suffix, so the session comes back under a name that changed
    /// without meaning anything (see <see cref="SessionName"/>). <paramref name="title"/> is
    /// the session's own title, which is what it deserves to be called instead.
    ///
    /// The name goes in under <c>--name</c>, never as the optional argument to
    /// <c>--remote-control</c>. The two are separate flags inside the CLI and only
    /// <c>--name</c> reaches the registry: a name passed to <c>--remote-control</c> is
    /// accepted, ignored, and the session comes back derived anyway.
    /// </summary>
    public static string ResumeCommand(LiveSession live, string? title = null)
    {
        var name = SessionName.IsChosen(live)
            ? live.Name!
            : SessionName.For(live.SessionId, live.Cwd, title);

        // --name is written only to the live registry, never to the transcript, so naming a
        // session here cannot shadow the title the app reads off its file.
        var command = $"claude --resume {live.SessionId} --name {SessionName.Quote(name)}";
        return live.RemoteControl ? $"{command} --remote-control" : command;
    }

    /// <summary>
    /// The full line typed into the terminal the session vacated: back to its folder first,
    /// because the shell may have been left somewhere else before Claude was started.
    /// </summary>
    public static string RelaunchLine(LiveSession live, string? title = null) =>
        live.Cwd is { Length: > 0 } cwd
            ? $"cd {SessionName.Quote(cwd)}; {ResumeCommand(live, title)}"
            : ResumeCommand(live, title);


    private static string Host(string entrypoint) => entrypoint switch
    {
        "claude-desktop" => "the desktop app",
        "sdk-cli" => "the SDK",
        "" => "an unknown host",
        _ => entrypoint,
    };
}
