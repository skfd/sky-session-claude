using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The check a done-declaration runs and records (NEXT.md, decided 2026-11-09). What has to
/// hold: a repo without a definition is never checked, a definition is not inherited across
/// a git root, a broken mechanism is "not-run" and never "failed", and the result survives
/// the store.
/// </summary>
public class DoneCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"donecheck-{Guid.NewGuid():N}");

    public DoneCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Repo(string name, string? checkJson = null)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        if (checkJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            File.WriteAllText(Path.Combine(root, DoneCheck.DefinitionPath), checkJson);
        }
        return root;
    }

    private static CheckDefinition Def(string command, int timeout = 60) =>
        new() { Command = command, Root = Path.GetTempPath(), TimeoutSeconds = timeout };

    [Theory]
    [InlineData(Declared.NeedsRead, true)]
    [InlineData(Declared.Exhausted, true)]
    [InlineData(Declared.Runnable, false)]
    [InlineData(Declared.Blocked, false)]
    public void OnlyDoneStatesAreChecked(Declared state, bool applies) =>
        Assert.Equal(applies, DoneCheck.Applies(state));

    [Fact]
    public void ARepoWithoutADefinitionIsNeverChecked() =>
        Assert.Null(DoneCheck.Find(Repo("plain")));

    [Fact]
    public void ASubfolderFindsItsRepoDefinition()
    {
        var root = Repo("withcheck", """{ "command": "dotnet test", "timeoutSeconds": 120 }""");
        var sub = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;

        var def = DoneCheck.Find(sub)!;
        Assert.Equal("dotnet test", def.Command);
        Assert.Equal(120, def.TimeoutSeconds);
        Assert.Equal(root, def.Root);
    }

    [Fact]
    public void ANestedRepoDoesNotInheritTheOuterCheck()
    {
        var outer = Repo("outer", """{ "command": "exit 1" }""");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".git"), "gitdir: elsewhere"); // a worktree's .git is a file

        Assert.Null(DoneCheck.Find(inner));
    }

    [Fact]
    public void ADefinitionWithoutACommandThrowsRatherThanReadingAsNone() =>
        Assert.Throws<FormatException>(() => DoneCheck.Parse("""{ "timeoutSeconds": 5 }""", _dir));

    [Fact]
    public void TimeoutDefaultsWhenMissing() =>
        Assert.Equal(DoneCheck.DefaultTimeoutSeconds, DoneCheck.Parse("""{ "command": "x" }""", _dir).TimeoutSeconds);

    [Fact]
    public void ExitZeroPasses() =>
        Assert.Equal(CheckOutcome.Passed, DoneCheck.Run(Def("exit 0")).Outcome);

    [Fact]
    public void ANonZeroExitFailsAndKeepsTheTail()
    {
        var r = DoneCheck.Run(Def("Write-Output 'boom here'; exit 3"));
        Assert.Equal(CheckOutcome.Failed, r.Outcome);
        Assert.StartsWith("exit 3", r.Detail);
        Assert.Contains("boom here", r.Detail);
    }

    [Fact]
    public void ATimeoutIsNotRunNeverFailed()
    {
        var r = DoneCheck.Run(Def("Start-Sleep -Seconds 30", timeout: 1));
        Assert.Equal(CheckOutcome.NotRun, r.Outcome);
        Assert.StartsWith("timed out", r.Detail);
        Assert.True(r.Seconds < 15);
    }

    [Fact]
    public void TheResultSurvivesTheStore()
    {
        var store = Path.Combine(_dir, "store");
        Directory.CreateDirectory(store);
        var check = new CheckResult
        {
            Outcome = CheckOutcome.Failed,
            Commit = "abc1234",
            Dirty = true,
            At = DateTimeOffset.Now,
            Seconds = 12.5,
            Detail = "exit 1\nnope",
        };

        new DeclarationStore(store).Set("s1", new Declaration
        {
            State = Declared.Exhausted, AtTurn = "u1", At = DateTimeOffset.Now, Check = check,
        });

        var read = new DeclarationStore(store).Get("s1")!;
        Assert.Equal(CheckOutcome.Failed, read.Check!.Outcome);
        Assert.Equal("abc1234", read.Check.Commit);
        Assert.True(read.Check.Dirty);
        Assert.Equal(12.5, read.Check.Seconds);
        Assert.Equal("exit 1\nnope", read.Check.Detail);
    }

    [Fact]
    public void AClaimWithoutACheckStaysWithout()
    {
        var store = Path.Combine(_dir, "store2");
        Directory.CreateDirectory(store);
        new DeclarationStore(store).Set("s1", new Declaration { State = Declared.Runnable, AtTurn = "u1" });

        Assert.Null(new DeclarationStore(store).Get("s1")!.Check);
        Assert.DoesNotContain("check", File.ReadAllText(Path.Combine(store, "declarations.json")));
    }
}
