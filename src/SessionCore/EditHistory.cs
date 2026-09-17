namespace SessionCore;

/// <summary>
/// One reversible change to the marks: what each session was, what it became, and what to
/// call it when the status line reports the change going backwards or forwards again.
///
/// A map per side rather than "these ids became Done", because a keystroke over a mixed
/// selection collapses several marks into one. Pressing D over [Done, Abandoned, None] leaves
/// three sessions Done; putting that back means three different marks, and only a per-session
/// Before knows which.
/// </summary>
/// <param name="Before">What each session was marked before the change.</param>
/// <param name="After">What each session was marked after it.</param>
/// <param name="Label">The sentence the change reported when it happened.</param>
public sealed record MarkEdit(
    IReadOnlyDictionary<string, Disposition> Before,
    IReadOnlyDictionary<string, Disposition> After,
    string Label);

/// <summary>
/// The undo/redo stacks behind Ctrl+Z and Ctrl+Y.
///
/// Deliberately a pile of edits and nothing else: it holds no store, touches no file and knows
/// nothing about rows, so it tests without a window and the window keeps the one job of
/// applying what comes back. A second kind of edit later is a second record type here, not a
/// rewrite.
///
/// Two things it does not do, both on purpose:
/// <list type="bullet">
/// <item>It does not survive the process. The history is the shape of this sitting at the
/// window — what you just did and might not have meant — and a stack reloaded from disk a day
/// later would offer to undo a decision you have long since forgotten making.</item>
/// <item>It does not detect conflicts. The store has other writers (<c>SessionCli done</c> on
/// an agent's behalf), so a mark made elsewhere between your keystroke and your Ctrl+Z is
/// overwritten by the undo. Undo restores what you saw, which is what the gesture means; the
/// alternative — refusing to undo because something else moved — would be a puzzle with no
/// way out of it.</item>
/// </list>
/// </summary>
public sealed class EditHistory
{
    /// <summary>
    /// How many edits back you can reach. Well past a sitting's worth of keystrokes, and the
    /// cap is only here so a window left open for a week cannot grow without limit.
    /// </summary>
    public const int Capacity = 100;

    private readonly LinkedList<MarkEdit> _undo = new();
    private readonly Stack<MarkEdit> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Edits currently undoable, newest first — for tests and for a count to show.</summary>
    public int UndoDepth => _undo.Count;

    public int RedoDepth => _redo.Count;

    /// <summary>
    /// Record an edit the operator just made. That branches the history, so anything undone
    /// and not yet redone is dropped: redoing it would now land on top of a different change.
    /// </summary>
    public void Push(MarkEdit edit)
    {
        _undo.AddLast(edit);
        _redo.Clear();
        while (_undo.Count > Capacity) _undo.RemoveFirst();
    }

    /// <summary>
    /// Take the newest edit off the undo pile, ready to be reversed. The caller applies its
    /// <see cref="MarkEdit.Before"/>. Null when there is nothing to undo.
    /// </summary>
    public MarkEdit? Undo()
    {
        if (_undo.Last is not { } last) return null;

        _undo.RemoveLast();
        _redo.Push(last.Value);
        return last.Value;
    }

    /// <summary>
    /// Take back the last undone edit, ready to be applied again. The caller applies its
    /// <see cref="MarkEdit.After"/>. Null when there is nothing to redo.
    /// </summary>
    public MarkEdit? Redo()
    {
        if (_redo.Count == 0) return null;

        var edit = _redo.Pop();
        _undo.AddLast(edit);
        return edit;
    }

    /// <summary>Forget everything. Nothing calls it yet; it is what a "start over" would use.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
