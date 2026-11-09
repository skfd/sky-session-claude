using System.Reflection;
using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The 10-12 regression, pinned: Sky outlives the CLI calls that write its sidecars, so
/// whatever it does not re-read on the tick is frozen at whatever it held when it started.
/// </summary>
public class SharedStoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"shared-{Guid.NewGuid():N}");

    public SharedStoresTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AStateDeclaredAfterStartShowsOnTheNextReload()
    {
        var sky = new SharedStores(_dir);
        Assert.Null(sky.Declarations.Get("abc"));

        // `SessionCli state blocked`, in its own process, after Sky came up.
        new DeclarationStore(_dir).Set("abc", new Declaration
        {
            State = Declared.Blocked, Note = "pick test layers", AtTurn = "u1", At = DateTimeOffset.Now,
        });

        Assert.Equal(StoreChange.Declarations, sky.Reload());
        Assert.Equal(Declared.Blocked, sky.Declarations.Get("abc")!.State);
    }

    /// <summary>
    /// A store can be a property and still be forgotten in <see cref="SharedStores.Reload"/>,
    /// which is the 10-12 bug over again; so each one is written from outside and must come
    /// back flagged.
    /// </summary>
    [Theory]
    [InlineData(StoreChange.Names)]
    [InlineData(StoreChange.Dispositions)]
    [InlineData(StoreChange.Declarations)]
    [InlineData(StoreChange.Standings)]
    public void EveryStoreWrittenElsewhereComesBackFlagged(StoreChange which)
    {
        var sky = new SharedStores(_dir);
        var cli = new SharedStores(_dir);
        switch (which)
        {
            case StoreChange.Names: cli.Names.Record("abc", "a name", NameOrigin.Oracle); break;
            case StoreChange.Dispositions: cli.Dispositions.Set("abc", Disposition.Done); break;
            case StoreChange.Declarations:
                cli.Declarations.Set("abc", new Declaration
                {
                    State = Declared.Runnable, AtTurn = "u1", At = DateTimeOffset.Now,
                });
                break;
            case StoreChange.Standings:
                cli.Standings.Set(@"C:\Code\p", new ProjectStanding
                {
                    Standing = Standing.Parked, At = DateTimeOffset.Now,
                });
                break;
        }

        Assert.Equal(which, sky.Reload());
    }

    /// <summary>Every flag has a case above, so a fifth store cannot slip past the theory.</summary>
    [Fact]
    public void EveryFlagIsCoveredByTheTheory()
    {
        var covered = typeof(SharedStoresTests)
            .GetMethod(nameof(EveryStoreWrittenElsewhereComesBackFlagged))!
            .GetCustomAttributes<InlineDataAttribute>()
            .Select(a => (StoreChange)a.GetData(null!).Single()[0])
            .ToHashSet();
        var flags = Enum.GetValues<StoreChange>().Where(f => f != StoreChange.None);
        Assert.Empty(flags.Except(covered));
        var stores = typeof(SharedStores).GetProperties()
            .Count(p => p.PropertyType.GetMethod("ReloadIfChanged", Type.EmptyTypes) is not null);
        Assert.Equal(stores, flags.Count());
    }

    [Fact]
    public void NothingWrittenMeansNothingChanged()
    {
        var sky = new SharedStores(_dir);
        Assert.Equal(StoreChange.None, sky.Reload());
    }

    /// <summary>
    /// The class of bug, not just the instance: a store someone else writes, which the tick
    /// never re-reads. Any type here that can reload itself has to be one of SharedStores'.
    /// </summary>
    [Fact]
    public void EveryReloadableStoreIsShared()
    {
        var reloadable = typeof(SharedStores).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.GetMethod("ReloadIfChanged", Type.EmptyTypes) is not null)
            .ToHashSet();
        var shared = typeof(SharedStores)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .ToHashSet();

        Assert.Empty(reloadable.Except(shared).Select(t => t.Name));
    }
}
