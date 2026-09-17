using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The undo pile behind Ctrl+Z. What matters here is the order things come back in, that a
/// new edit throws away the redo branch, and that an edit carries enough to restore a mixed
/// selection — the case a plain "these ids became Done" would get wrong.
/// </summary>
public class EditHistoryTests
{
    private static MarkEdit Edit(string id, Disposition before, Disposition after) =>
        new(
            new Dictionary<string, Disposition> { [id] = before },
            new Dictionary<string, Disposition> { [id] = after },
            $"{id}: {before} -> {after}");

    [Fact]
    public void EmptyHistoryHasNothingToUndoOrRedo()
    {
        var history = new EditHistory();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
    }

    [Fact]
    public void UndoWalksBackNewestFirst()
    {
        var history = new EditHistory();
        history.Push(Edit("a", Disposition.None, Disposition.Done));
        history.Push(Edit("b", Disposition.None, Disposition.Abandoned));

        Assert.Equal("b: None -> Abandoned", history.Undo()!.Label);
        Assert.Equal("a: None -> Done", history.Undo()!.Label);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void RedoWalksForwardAgain()
    {
        var history = new EditHistory();
        history.Push(Edit("a", Disposition.None, Disposition.Done));
        history.Push(Edit("b", Disposition.None, Disposition.Abandoned));

        history.Undo();
        history.Undo();

        Assert.Equal("a: None -> Done", history.Redo()!.Label);
        Assert.Equal("b: None -> Abandoned", history.Redo()!.Label);
        Assert.False(history.CanRedo);
    }

    // The branch: undo two, then mark something new. Redoing what you undid would now land on
    // top of a change that was not there when you undid it, so it is gone.
    [Fact]
    public void ANewEditDropsTheRedoBranch()
    {
        var history = new EditHistory();
        history.Push(Edit("a", Disposition.None, Disposition.Done));
        history.Undo();
        Assert.True(history.CanRedo);

        history.Push(Edit("b", Disposition.None, Disposition.Abandoned));

        Assert.False(history.CanRedo);
        Assert.Equal(1, history.UndoDepth);
    }

    // An undone edit is redoable, and redoing it makes it undoable again — otherwise
    // Ctrl+Z, Ctrl+Y, Ctrl+Z would be a dead end.
    [Fact]
    public void RedoingPutsTheEditBackOnTheUndoPile()
    {
        var history = new EditHistory();
        history.Push(Edit("a", Disposition.None, Disposition.Done));

        history.Undo();
        history.Redo();

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("a: None -> Done", history.Undo()!.Label);
    }

    [Fact]
    public void OldestEditsFallOffThePileAtCapacity()
    {
        var history = new EditHistory();
        for (int i = 0; i < EditHistory.Capacity + 10; i++)
            history.Push(Edit($"s{i}", Disposition.None, Disposition.Done));

        Assert.Equal(EditHistory.Capacity, history.UndoDepth);

        // The newest survived; walking all the way back reaches s10, not s0.
        MarkEdit? last = null;
        while (history.Undo() is { } e) last = e;
        Assert.Equal("s10: None -> Done", last!.Label);
    }

    // A keystroke over a mixed selection is one edit, and its Before is what makes it
    // reversible: three sessions, three different marks to put back.
    [Fact]
    public void AnEditRemembersWhatEachSessionWas()
    {
        var before = new Dictionary<string, Disposition>
        {
            ["a"] = Disposition.Done,
            ["b"] = Disposition.Abandoned,
            ["c"] = Disposition.None,
        };
        var after = before.Keys.ToDictionary(id => id, _ => Disposition.Done);

        var history = new EditHistory();
        history.Push(new MarkEdit(before, after, "Marked 3 session(s) done."));

        var undone = history.Undo()!;
        Assert.Equal(Disposition.Done, undone.Before["a"]);
        Assert.Equal(Disposition.Abandoned, undone.Before["b"]);
        Assert.Equal(Disposition.None, undone.Before["c"]);
    }

    [Fact]
    public void ClearForgetsBothPiles()
    {
        var history = new EditHistory();
        history.Push(Edit("a", Disposition.None, Disposition.Done));
        history.Undo();
        history.Push(Edit("b", Disposition.None, Disposition.Done));

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}
