namespace SessionCore;

/// <summary>
/// Decides which <c>claude rc</c> hosts may be restarted unattended.
///
/// The sibling of <see cref="RestartPolicy"/>, and it exists as its own rule because a host
/// answers none of the questions that one asks. It publishes no registry entry, so there is
/// no build to compare and no busy/idle state to read; it is not in a terminal anybody types
/// into, so "you may be mid-draft" cannot happen to it. On its own, a host looks like the
/// safest thing on the machine to restart.
///
/// It is not, and the reason is what it is serving. A host is a server that spawns
/// conversations, and those <em>do</em> publish state — so the question "is anything in
/// flight here" is answered one level down, across every session the host has spawned, and
/// the worst answer among them is the host's. A host serving a busy conversation is as
/// unsafe as a busy session; a host serving nothing at all is the safest case there is.
///
/// The children are counted from the process tree rather than from the registry, because
/// the gap between the two is exactly the case that matters: a conversation that has started
/// but not yet published anything is invisible to a registry sweep and would be swept away
/// mid-birth. More claude children than registry rows means something is running that cannot
/// be asked how it is, which is the one answer this refuses to guess at.
/// </summary>
public static class HostRestartPolicy
{
    /// <summary>One conversation a host is serving: what the registry says, and what its file ends on.</summary>
    public readonly record struct Served(LiveSession Live, SessionStatus? Tail);

    /// <summary>
    /// Judge one host. <paramref name="serving"/> is every live session it spawned,
    /// <paramref name="childProcesses"/> is how many claude processes sit under it in the
    /// tree, and <paramref name="now"/> is passed in so the rule stays testable.
    /// </summary>
    public static SweepVerdict Judge(
        RemoteControlHost host,
        IReadOnlyList<Served> serving,
        int childProcesses,
        DateTime now)
    {
        // --- is anything in flight? ------------------------------------------
        // Unsafe first and in full, so the reason reported is the worst one true of it
        // rather than whichever conversation happened to be listed first.
        foreach (var (live, tail) in serving)
        {
            if (string.Equals(live.Status, "busy", StringComparison.OrdinalIgnoreCase))
                return new(SweepSafety.Unsafe, "a conversation it is serving has a turn in flight");

            if (live.Status is null)
                return new(SweepSafety.Unsafe,
                    "a conversation it is serving publishes no busy/idle state, so we cannot tell if it is mid-turn");

            if (tail is SessionStatus.WaitingAgent or SessionStatus.CutOff)
                return new(SweepSafety.Unsafe,
                    $"a conversation it is serving stopped mid-step ({tail.Value.ToWire()})");

            if (!string.Equals(live.Status, "idle", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(live.Status, "waiting", StringComparison.OrdinalIgnoreCase))
                return new(SweepSafety.Unsafe,
                    $"a conversation it is serving is in an unrecognised state \"{live.Status}\"");
        }

        if (childProcesses > serving.Count)
            return new(SweepSafety.Unsafe,
                "it has just spawned something that has not said what it is doing yet");

        // --- could a restart still cost something? ---------------------------
        foreach (var (live, tail) in serving)
        {
            // Waiting is the phone's version of a half-typed reply: the answer it wants is
            // one tap away on a screen we cannot see, and a restart drops the question.
            if (string.Equals(live.Status, "waiting", StringComparison.OrdinalIgnoreCase))
                return new(SweepSafety.Ask,
                    "a conversation it is serving is waiting on an answer from you");

            if (live.StatusUpdatedAt is { } since && now - since < RestartPolicy.SettleTime)
                return new(SweepSafety.Ask, "a conversation it is serving only just went idle");

            if (tail is SessionStatus.WaitingYou)
                return new(SweepSafety.Ask, "a conversation it is serving asked you something");
        }

        return new(SweepSafety.Safe, serving.Count == 0
            ? "serving nothing"
            : $"serving {Conversations(serving.Count)}, idle and settled");
    }

    private static string Conversations(int count) =>
        count == 1 ? "one conversation" : $"{count} conversations";
}
