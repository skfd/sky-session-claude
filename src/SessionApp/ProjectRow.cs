using System.ComponentModel;
using SessionCore;

namespace SessionApp;

/// <summary>
/// Thin display wrapper over a <see cref="ProjectRoll"/>, the way <see cref="SessionRow"/>
/// wraps a <see cref="SessionInfo"/>. Same contract: view-specific formatting lives here,
/// and <see cref="Roll"/> is swappable so a refresh updates a row in place instead of
/// rebuilding the list and dropping the selection.
///
/// A project row is two lines where a session card is four, and the reason is what each
/// list is for. A card is read to find a conversation again, so it carries the prompt and
/// the recap. A project row is read to decide what to do tonight, so it carries one state
/// and one note — the note being the only part nobody could work out from the files.
/// </summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    private ProjectRoll _roll;

    public ProjectRow(ProjectRoll roll) => _roll = roll;

    public ProjectRoll Roll
    {
        get => _roll;
        set
        {
            _roll = value;
            // Null name signals "all properties changed", matching SessionRow.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Project => _roll.Project;
    public string Folder => _roll.Folder;
    public ProjectState State => _roll.State;

    /// <summary>The state as the docs and the CLI spell it — <c>needs-read</c>, not NeedsRead.</summary>
    public string StateWord => ProjectFold.ToWire(_roll.State);

    /// <summary>
    /// What an agent said about the session this state came from, or a plain description of
    /// what was worked out when nothing was declared. The line the operator actually reads.
    /// </summary>
    public string Note => _roll.Note is { Length: > 0 } note ? note : DerivedNote(_roll.State);

    private static string DerivedNote(ProjectState state) => state switch
    {
        ProjectState.Broken => "a session here died mid-work — reviving it needs no decision",
        ProjectState.Runnable => "work is queued and needs nothing from you",
        ProjectState.Undeclared => "something here is unfinished and nobody said what it needs",
        ProjectState.Quiet => "nothing pending",
        ProjectState.Abandoned => "every session here is crossed out",
        // Blocked, NeedsRead and Exhausted are declared-only, so they always carry a note.
        _ => "",
    };

    /// <summary>Whether this project wants anything at all — what the list shows by default.</summary>
    public bool WantsSomething => _roll.State
        is ProjectState.Broken or ProjectState.Blocked
        or ProjectState.NeedsRead or ProjectState.Runnable or ProjectState.Undeclared;

    /// <summary>
    /// The counts, in the card's own dot-separated shape. Unattended is named only when there
    /// is any, because on most projects it is zero and a zero would be noise — but on the one
    /// driving Claude as a library it is the most informative number on the row.
    /// </summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string> { $"{_roll.Sessions} session{(_roll.Sessions == 1 ? "" : "s")}" };
            if (_roll.Unfinished > 0) parts.Add($"{_roll.Unfinished} unfinished");
            if (_roll.Declared > 0) parts.Add($"{_roll.Declared} declared");
            if (_roll.Abandoned > 0) parts.Add($"{_roll.Abandoned} crossed out");
            if (_roll.Unattended > 0) parts.Add($"{_roll.Unattended} unattended");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>When anything here last moved, in the same relative words the cards use.</summary>
    public string Age => TextUtil.RelativeAge(_roll.LastActive);

    public string LastActiveTooltip => _roll.LastActive == default
        ? ""
        : _roll.LastActive.ToString("dddd d MMMM yyyy, HH:mm");
}
