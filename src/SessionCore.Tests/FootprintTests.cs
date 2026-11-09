using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// What each writer leaves behind after a lot of use, not what one write does. The 10-28
/// regression passed every single-write test: a trust grant wrote the config correctly and a
/// stamped backup beside it, and nothing asked what two dozen grants would leave in the home
/// folder. Each case here runs a writer many times against an empty folder and fails on any
/// file it did not say it would keep.
/// </summary>
public class FootprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"footprint-{Guid.NewGuid():N}");

    public FootprintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string[] Left() =>
        Directory.GetFileSystemEntries(_dir).Select(p => Path.GetFileName(p)!).Order().ToArray();

    private const int Many = 40;

    [Fact]
    public void SharedStoresLeaveOnlyTheirOwnFiles()
    {
        // Two instances, as Sky and SessionCli are, taking turns.
        var a = new SharedStores(_dir);
        var b = new SharedStores(_dir);
        for (var i = 0; i < Many; i++)
        {
            var who = i % 2 == 0 ? a : b;
            var id = $"s{i % 7}";
            who.Dispositions.Set(id, i % 3 == 0 ? Disposition.Done : Disposition.None);
            who.Names.Record(id, $"name {i}", NameOrigin.Oracle);
            who.Declarations.Set(id, new Declaration
            {
                State = Declared.Runnable, AtTurn = $"u{i}", At = DateTimeOffset.Now,
            });
            who.Standings.Set($@"C:\Code\p{i % 5}", new ProjectStanding
            {
                Standing = i % 2 == 0 ? Standing.Parked : Standing.Active, At = DateTimeOffset.Now,
            });
            if (i % 9 == 0) who.Names.Forget(id);
            a.Reload();
            b.Reload();
        }

        var allowed = new[] { "declarations.json", "dispositions.json", "names.json", "standings.json" };
        Assert.Empty(Left().Except(allowed));
    }

    [Fact]
    public void TrustGrantsKeepAtMostThreeBackups()
    {
        var config = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(config, """{ "projects": {} }""");

        // Grants a second or more apart would stamp distinct backups; seed old stamps instead
        // of sleeping, so the run is as long as the pile it would have made.
        for (var i = 0; i < 12; i++)
            File.WriteAllText($"{config}.bak-202609{i + 10:00}-120000", "{}");
        for (var i = 0; i < 6; i++)
            Assert.True(ClaudeTrust.Grant($@"C:\Code\repo-{i}", config).Ok);

        var left = Left();
        Assert.DoesNotContain(left, f => f != ".claude.json" && !f.StartsWith(".claude.json.bak-"));
        Assert.InRange(left.Count(f => f.StartsWith(".claude.json.bak-")), 1, 3);
    }
}
