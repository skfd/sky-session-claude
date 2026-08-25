namespace SessionCore;

/// <summary>
/// Everything the policy needs to decide what a session should be called. Assembled by the
/// caller that has the session file and the registry; the policy itself reads nothing.
/// </summary>
public sealed record NameInputs
{
    public required string SessionId { get; init; }

    /// <summary>Where the session ran. The repo is taken from it, not the worktree.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// The <c>custom-title</c> in the transcript — what a <c>--name</c> launch or a rename
    /// wrote. For a session that is not running this is the only name it has.
    /// </summary>
    public string? CustomTitle { get; init; }

    /// <summary>The model-written <c>aiTitle</c>: free, offline, and generated only once.</summary>
    public string? AiTitle { get; init; }

    /// <summary>The registry entry, when the session is open in a terminal.</summary>
    public LiveSession? Live { get; init; }

    /// <summary>
    /// What every live session is called right now, this one included. Collisions are a
    /// live-only question: two identical rows in the list are what fails to identify
    /// anything, and a session that closed months ago is not in that list.
    /// </summary>
    public IReadOnlyCollection<string> LiveNames { get; init; } = [];

    /// <summary>
    /// A subject an oracle read out of the transcript, when the caller was willing to pay
    /// for one (see <see cref="NamePolicy.WantsOracle"/>). Null in the ordinary case.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// Whether the session has done anything at all. A terminal opened and never used has
    /// no subject to find, so the floor is the honest answer rather than a failure.
    /// </summary>
    public bool HasContent { get; init; }
}

/// <summary>What to call a session, and why. A null <see cref="Name"/> means leave it alone.</summary>
public readonly record struct NameDecision(string? Name, NameOrigin? Origin, string Why)
{
    public bool HasName => !string.IsNullOrEmpty(Name);

    public static NameDecision Nothing(string why) => new(null, null, why);
}

/// <summary>
/// The single decider. Every path that writes a name — a launch under <c>--name</c>, a
/// rename over a live session's pipe, the app's background pass — asks this and then
/// executes what it is handed. Nothing else works out a name for itself; a path that did
/// would be a second policy, and the two would disagree the moment either changed.
///
/// Two properties are what make it safe to run unattended:
///
/// <list type="bullet">
/// <item><b>It is a fixed point.</b> Fed the state immediately after its own rename it
/// returns nothing. That matters because renaming writes a <c>custom-title</c> into the
/// transcript, which wakes the app's file watcher, which runs this again — so anything less
/// than a fixed point is a rename loop rather than a background pass.</item>
/// <item><b>It only ever improves.</b> A name is replaced only by one from a strictly better
/// source (<see cref="NameOrigin"/> is that ladder), so a session that named itself is never
/// demoted to its <c>aiTitle</c>, and neither is ever demoted to the floor.</item>
/// </list>
/// </summary>
public static class NamePolicy
{
    /// <summary>
    /// What <paramref name="inputs"/> should be called, or nothing when the name it has is
    /// already the best available.
    /// </summary>
    public static NameDecision Decide(NameInputs inputs, NameStore store)
    {
        var current = CurrentName(inputs);
        var origin = OriginOf(inputs, store, current);
        var best = Best(inputs);

        if (origin == NameOrigin.Chosen)
        {
            // The one place Sky overwrites something you typed. Two live sessions reading
            // the same thing identify neither, so the subject wins — but only when there is
            // a subject: replacing "vagabond maps" with "vagabond-map-69" would trade a
            // collision for a name that says even less, which is not a repair.
            if (!Collides(inputs, current))
                return NameDecision.Nothing("the name is yours");

            if (best.Origin == NameOrigin.Floor)
                return NameDecision.Nothing(
                    "it shares its name with another live session, but there is nothing better to call it");

            return new(best.Name, best.Origin,
                $"it shares \"{current}\" with another live session, and this one is about {Subjectish(best.Name)}");
        }

        // Absent counts as worse than anything: a session with no name at all takes whatever
        // is going, including the floor.
        int currentRank = current is { Length: > 0 } ? (int)origin!.Value : int.MaxValue;
        int bestRank = (int)best.Origin;

        if (bestRank > currentRank)
            return NameDecision.Nothing($"nothing better than the {Describe(origin!.Value)} it has");

        if (bestRank == currentRank && string.Equals(best.Name, current, StringComparison.Ordinal))
            return NameDecision.Nothing("already named that");

        return new(best.Name, best.Origin, Why(origin, best.Origin));
    }

    /// <summary>
    /// Whether paying <c>claude -p</c> to read this session would help.
    ///
    /// True only where the free sources have run out: the session has done something, has no
    /// <c>aiTitle</c> to compose from, and is sitting on a placeholder — or on a name of
    /// yours that collides, which is the one case where a chosen name still needs a subject
    /// to replace it. A session that named itself, or already carries a name an oracle
    /// produced, is never asked about twice.
    /// </summary>
    public static bool WantsOracle(NameInputs inputs, NameStore store)
    {
        if (!inputs.HasContent) return false;

        // Anything but the floor means a free source is available and the call is waste.
        if (Best(inputs with { Subject = null }).Origin != NameOrigin.Floor) return false;

        var current = CurrentName(inputs);
        var origin = OriginOf(inputs, store, current);

        if (origin == NameOrigin.Chosen) return Collides(inputs, current);
        return origin is null or NameOrigin.Floor;
    }

    // --- what it has now ----------------------------------------------------

    /// <summary>
    /// The name the session answers to: the registry's while it runs, and otherwise the
    /// <c>custom-title</c> in its transcript. Not its <c>aiTitle</c> — that is a title the
    /// app displays, never a name the session was launched under.
    /// </summary>
    private static string? CurrentName(NameInputs inputs) =>
        inputs.Live?.Name is { Length: > 0 } live ? live
        : inputs.CustomTitle is { Length: > 0 } custom ? custom
        : null;

    /// <summary>
    /// Where the name it has now came from, or null when it has none.
    ///
    /// The store is the primary answer and the only reliable one. The two fallbacks are for
    /// names written before the store existed: the registry's own <c>nameSource</c>, which
    /// is present exactly when the CLI invented the name, and the shape of the name itself.
    /// </summary>
    private static NameOrigin? OriginOf(NameInputs inputs, NameStore store, string? current)
    {
        if (string.IsNullOrEmpty(current)) return null;

        if (store.OriginOf(inputs.SessionId, current) is { } recorded) return recorded;

        // nameSource present means the CLI derived it — a placeholder by construction, and
        // the one case that needs no guessing.
        if (inputs.Live is { } live && !SessionName.IsChosen(live)) return NameOrigin.Floor;

        // Last resort, and fallible: a name of yours that happens to read like "repo-XX" is
        // taken for a placeholder. See SessionName.IsFloor.
        if (SessionName.IsFloor(current, inputs.SessionId, inputs.Cwd)) return NameOrigin.Floor;

        return NameOrigin.Chosen;
    }

    /// <summary>
    /// True when another live session answers to the same name. The session's own row is in
    /// <see cref="NameInputs.LiveNames"/>, so a collision is the second match, not the first.
    /// </summary>
    private static bool Collides(NameInputs inputs, string? current)
    {
        if (inputs.Live is null || string.IsNullOrEmpty(current)) return false;

        int seen = 0;
        foreach (var name in inputs.LiveNames)
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && ++seen > 1)
                return true;

        return false;
    }

    // --- the best it could have ---------------------------------------------

    /// <summary>
    /// The best name available from what the caller brought, and where it came from. Falls
    /// through to the floor, which always produces something.
    /// </summary>
    private static (string Name, NameOrigin Origin) Best(NameInputs inputs)
    {
        // A subject was read out of the conversation just now; an aiTitle was written once,
        // early, and never revisited. The fresher one wins when the caller paid for it.
        if (Composed(inputs.Subject, inputs) is { } paid) return (paid, NameOrigin.Oracle);
        if (Composed(inputs.AiTitle, inputs) is { } free) return (free, NameOrigin.Title);

        return (SessionName.Floor(inputs.SessionId, inputs.Cwd), NameOrigin.Floor);
    }

    /// <summary>
    /// <paramref name="subject"/> as a name, or null when it is not one. A subject that is
    /// itself a placeholder — the slug a previous Sky wrote back into the transcript — is
    /// refused here, which is what stops a placeholder from being composed into a title and
    /// made permanent.
    /// </summary>
    private static string? Composed(string? subject, NameInputs inputs)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        if (SessionName.IsFloor(subject, inputs.SessionId, inputs.Cwd)) return null;

        return SessionName.Compose(subject, inputs.Cwd) is { Length: > 0 } name ? name : null;
    }

    // --- saying why ---------------------------------------------------------

    private static string Why(NameOrigin? from, NameOrigin to) => (from, to) switch
    {
        (null, NameOrigin.Floor) => "it has no name and nothing to say about itself yet",
        (null, _) => "it has no name yet",
        (NameOrigin.Floor, NameOrigin.Floor) => "its name is a placeholder that changes on every restart",
        (NameOrigin.Floor, _) => "it was named after its folder, and the conversation says more",
        _ => $"a {Describe(to)} is better than the {Describe(from!.Value)} it has",
    };

    private static string Describe(NameOrigin origin) => origin switch
    {
        NameOrigin.Chosen => "name you chose",
        NameOrigin.SelfNamed => "name it chose for itself",
        NameOrigin.Title => "name from its own title",
        NameOrigin.Oracle => "name read out of the conversation",
        _ => "placeholder",
    };

    /// <summary>The subject half of a composed name, for a sentence that quotes it back.</summary>
    private static string Subjectish(string name)
    {
        int cut = name.LastIndexOf(" — ", StringComparison.Ordinal);
        return cut > 0 ? $"\"{name[..cut]}\"" : $"\"{name}\"";
    }
}
