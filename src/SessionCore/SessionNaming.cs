namespace SessionCore;

/// <summary>
/// The seam between deciding a name and writing one.
///
/// <see cref="NamePolicy"/> decides; <see cref="SessionRenamer"/> and the launch verbs write.
/// This is what holds them together, and it exists for one invariant: <b>every name Sky
/// writes is recorded to <see cref="NameStore"/> in the same operation.</b> A launch under
/// <c>--name</c>, a rename over the pipe, the app's background pass — all of them. A write
/// that skips the record puts the name back where it started, indistinguishable from one the
/// operator typed and therefore frozen forever, which is the bug the whole design is for.
///
/// So the paths that write a name go through here rather than composing one themselves. The
/// planning half is separate, for the callers that only want to say what would happen.
/// </summary>
public static class SessionNaming
{
    // --- what the policy needs to see ---------------------------------------

    /// <summary>Everything a scanned session knows about itself, plus its registry row.</summary>
    public static NameInputs InputsFor(
        SessionInfo info, LiveSession? live, IReadOnlyCollection<string>? liveNames = null,
        string? subject = null) => new()
        {
            SessionId = info.SessionId,

            // RealCwd, not Cwd: the scanner fills Cwd with a sentence when the file recorded
            // no folder, and a sentence slugs into a folder name just as happily as a path.
            Cwd = live?.Cwd ?? info.RealCwd,
            CustomTitle = info.CustomTitle,
            AiTitle = info.AiTitle,
            Live = live,
            LiveNames = liveNames ?? [],
            Subject = subject,
            HasContent = info.HasContent,
        };

    /// <summary>
    /// A session known only from the registry, because its file has not been scanned — the
    /// sweep path, where scanning every transcript to name one session would be the expensive
    /// half of a cheap operation.
    /// </summary>
    public static NameInputs InputsFor(
        LiveSession live, IReadOnlyCollection<string>? liveNames = null, string? subject = null) => new()
        {
            SessionId = live.SessionId,
            Cwd = live.Cwd,
            Live = live,
            LiveNames = liveNames ?? [],
            Subject = subject,

            // Nothing was read, so nothing can be claimed. Without a title or a subject the
            // policy can only reach the floor, and saying the session is empty would let it
            // do that to a session full of work.
            HasContent = subject is { Length: > 0 },
        };

    /// <summary>Every live session's current name, which is the collision set.</summary>
    public static IReadOnlyCollection<string> LiveNamesOf(Dictionary<string, List<LiveSession>> live) =>
        live.Values
            .SelectMany(v => v)
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

    // --- renaming a session that is running ---------------------------------

    /// <summary>
    /// Send the rename, and record it as Sky's.
    ///
    /// The record is written whenever the name actually landed, which is not quite the same as
    /// the rename reporting success: confirmation is a poll of the registry, and a session slow
    /// to publish would otherwise end up carrying a Sky name with nothing to say so. That is
    /// the masquerade bug, and it costs one extra registry read to close.
    /// </summary>
    public static async Task<RenameResult> RenameAsync(
        LiveSession live, string name, NameOrigin origin, NameStore store)
    {
        var result = await SessionRenamer.RenameAsync(live, name);

        if (result.Ok || Landed(live, name))
            store.Record(live.SessionId, name, origin);

        return result;
    }

    private static bool Landed(LiveSession live, string name)
    {
        try
        {
            return LiveSessionRegistry.ReadOne(live.Pid) is { } current
                && string.Equals(current.SessionId, live.SessionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Name, name, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // --- naming a session on the way back up --------------------------------

    /// <summary>
    /// What a session would be launched under, and whether that is a name Sky is choosing or
    /// one it is leaving alone. Records nothing, so a dry run can say what it would do.
    ///
    /// Unlike <see cref="NamePolicy.Decide"/> this always produces a name: a command line has
    /// to say something, and "leave it as it is" means passing the name it already has rather
    /// than passing nothing and letting the CLI re-derive one with a fresh suffix.
    /// </summary>
    public static NameDecision PlanLaunch(NameInputs inputs, NameStore store)
    {
        var decision = NamePolicy.Decide(inputs, store);
        if (decision.HasName) return decision;

        // Already the best it could have. Carry it over untouched, and record nothing: either
        // it is the operator's, or it is already recorded as ours.
        var current = inputs.Live?.Name is { Length: > 0 } live ? live
            : inputs.CustomTitle is { Length: > 0 } custom ? custom
            : null;

        return current is not null
            ? new NameDecision(current, null, decision.Why)
            : new NameDecision(SessionName.Floor(inputs.SessionId, inputs.Cwd), NameOrigin.Floor,
                "it has no name and nothing to say about itself yet");
    }

    /// <summary>
    /// The same, for a launch that is actually happening: the name to pass under
    /// <c>--name</c>, recorded as Sky's in the same breath.
    ///
    /// This is the call the plan singles out as the one most likely to be missed. A launch
    /// path that works out its own name instead re-freezes Sky's old placeholders on every
    /// restart, and the store buys nothing.
    /// </summary>
    public static string NameForLaunch(NameInputs inputs, NameStore store)
    {
        var decision = PlanLaunch(inputs, store);

        if (decision.Origin is { } origin && decision.Name is { Length: > 0 } name)
            store.Record(inputs.SessionId, name, origin);

        return decision.Name!;
    }
}
