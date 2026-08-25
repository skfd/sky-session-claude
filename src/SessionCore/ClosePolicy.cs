namespace SessionCore;

/// <summary>
/// Decides which live sessions may be closed unattended — the end-of-day sweep.
///
/// Closing asks the same process-level question a restart does, and
/// <see cref="RestartPolicy.Judge"/> already answers it: nothing may be in flight, nothing
/// may be half-typed, nothing may be waiting on an approval. Everything that policy calls
/// unsafe is unsafe here too, for the same reasons, so this one starts from that verdict
/// rather than restating it.
///
/// What close adds is the question a restart never has to ask. A restart puts the session
/// back; the terminal you were looking at is still there afterwards. A close takes it away,
/// and an open terminal is how unfinished work announces itself in the morning. So the
/// sweep wants two things, not one: the process must be quiet, <em>and</em> the work must
/// be over — the file ended on <see cref="SessionStatus.Complete"/>, or you ticked it off
/// yourself. Idle-but-unfinished is offered, never taken.
///
/// A mark can promote a verdict from Ask to Safe but never from Unsafe: "I'm done with
/// this" is a statement about the conversation, not about the turn that is still running
/// inside it.
/// </summary>
public static class ClosePolicy
{
    /// <summary>
    /// Judge one live session. <paramref name="tail"/> is the status the scanner read from
    /// the session file, <paramref name="disposition"/> is what you marked it, and
    /// <paramref name="now"/> is passed in so the rule is testable rather than clock-dependent.
    ///
    /// A null tail is two different facts, and <paramref name="scanned"/> is which. With
    /// <c>scanned: false</c> nobody looked, and the work may be anything. With
    /// <c>scanned: true</c> the files were all read and this session is in none of them —
    /// which can only mean it has never been prompted, and that is the emptiest thing a
    /// terminal can be holding.
    /// </summary>
    public static SweepVerdict Judge(
        LiveSession live, SessionStatus? tail, Disposition disposition, DateTime now,
        bool scanned = false)
    {
        // Everything a restart refuses outright, a close refuses too, and for the same reason.
        var quiet = RestartPolicy.Judge(live, tail, now);
        if (quiet.Safety == SweepSafety.Unsafe) return quiet;

        // Judged again with the file set aside: whatever is still short of Safe is about the
        // running process — an approval pending, an idle too fresh to trust — and no mark of
        // yours answers it. Only the file's doubts are yours to overrule.
        var process = RestartPolicy.Judge(live, null, now);
        if (process.Safety != SweepSafety.Safe) return process;

        // The process is quiet. All that is left is whether the work is over.
        if (disposition is Disposition.Done or Disposition.Abandoned)
            return new(SweepSafety.Safe, $"marked {Mark(disposition)}, and idle");

        if (tail is SessionStatus.Complete) return new(SweepSafety.Safe, "finished and idle");

        // A terminal opened this morning and never typed into. There is no conversation to
        // lose, and it is the purest form of what an end-of-day sweep is for.
        if (tail is null)
            return scanned
                ? new(SweepSafety.Safe, "never prompted — nothing was said in it")
                : new(SweepSafety.Ask, "no session file was read, so nothing says its work is finished");

        // Waiting on you, interrupted, an error, a rate limit: the process is quiet but the
        // work is not over. Closing loses the only reminder that it is not — so these go in
        // the report, not the sweep.
        return new(SweepSafety.Ask,
            $"idle, but unfinished ({tail.Value.ToWire()}) — the open terminal is the reminder");
    }

    private static string Mark(Disposition disposition) =>
        disposition == Disposition.Done ? "done" : "abandoned";
}
