namespace SessionCore;

/// <summary>Which of the <see cref="SharedStores"/> came back different from a reload.</summary>
[Flags]
public enum StoreChange
{
    None = 0,
    Names = 1,
    Dispositions = 2,
    Declarations = 4,
    Standings = 8,
}

/// <summary>
/// Every sidecar a long-lived reader has to keep re-reading, because someone else writes it:
/// SessionCli on an agent's behalf, a session renaming itself, another Sky.
///
/// They live together so that "reload on the tick" is one call, not one call per store that
/// each new store has to remember to add. Forgetting is exactly how the 10-12 regression
/// shipped: <see cref="DeclarationStore.ReloadIfChanged"/> had existed and been tested for
/// six weeks, the app simply never called it, and every state declared after Sky started was
/// missing from its project rows. SharedStoresTests holds the line two ways: a declaration
/// written by another process shows up on the next <see cref="Reload"/>, and any store in
/// this assembly with a <c>ReloadIfChanged</c> that is not one of these properties fails.
/// </summary>
public sealed class SharedStores
{
    public SharedStores() : this(DispositionStore.DefaultDir()) { }

    public SharedStores(string dir)
    {
        Dispositions = new DispositionStore(dir);
        Names = new NameStore(dir);
        Declarations = new DeclarationStore(dir);
        Standings = new ProjectStandingStore(dir);
    }

    public DispositionStore Dispositions { get; }
    public NameStore Names { get; }
    public DeclarationStore Declarations { get; }
    public ProjectStandingStore Standings { get; }

    /// <summary>
    /// Re-read whichever stores changed on disk since the last look; one stat each when none
    /// did. The answer says which ones changed, so the caller refreshes only what they feed.
    /// </summary>
    public StoreChange Reload()
    {
        var changed = StoreChange.None;
        if (Names.ReloadIfChanged()) changed |= StoreChange.Names;
        if (Dispositions.ReloadIfChanged()) changed |= StoreChange.Dispositions;
        if (Declarations.ReloadIfChanged()) changed |= StoreChange.Declarations;
        if (Standings.ReloadIfChanged()) changed |= StoreChange.Standings;
        return changed;
    }
}
