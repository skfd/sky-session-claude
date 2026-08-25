using System.Threading;
using SessionCore;

namespace SessionApp;

/// <summary>
/// What happens when someone clicks a <c>skysession://</c> link.
///
/// The registry points at this exe, so Windows starts a copy of the app carrying the URL.
/// That copy does the work itself and exits: it never claims the single-instance slot and
/// never builds the main window. The alternative — hand the URL to the instance already
/// running — needs a channel that carries a payload, and the one this app has (an
/// <see cref="EventWaitHandle"/> that means "come forward") carries nothing. Adding one
/// would buy nothing either, because every decision below already lives in SessionCore and
/// is the same decision wherever it runs. Two writers, one set of verbs underneath, which is
/// exactly how the brief's inbox and this coexist.
///
/// What the design calls rule 8 — do not start a second window — is kept: nothing here shows
/// a main window, and the process is gone in a second.
///
/// Every branch says something. A resume opens a terminal, which is its own receipt; a
/// <c>done</c> has no visible result of its own, so it either brings the running window
/// forward to show the card struck through, or says so itself when there is no window to
/// bring. A refusal is always shown, because a link Chrome quietly declined and a link that
/// worked must not look the same.
/// </summary>
internal static class LinkHandler
{
    /// <summary>The argument Windows passes for a registered scheme, if this launch got one.</summary>
    public static string? UrlIn(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(SessionUri.Scheme + ":", StringComparison.OrdinalIgnoreCase));

    public static void Handle(string url)
    {
        var roots = LinkRoots.Load();
        var request = SessionUri.Parse(url, roots.Roots);

        if (!request.Ok)
        {
            LinkDialog.Notice(
                "That link was not accepted.",
                request.Refusal + (roots.Warning is { Length: > 0 } w ? $"\n\n{w}." : ""));
            return;
        }

        switch (request.Verb)
        {
            case SessionUriVerb.Resume: Resume(request.Id!); return;
            case SessionUriVerb.Done: Done(request.Id!); return;
            case SessionUriVerb.New: New(request.Folder!); return;
        }
    }

    // --- resume -------------------------------------------------------------

    private static void Resume(string idOrPrefix)
    {
        if (Find(idOrPrefix) is not { } info) return;

        var plan = SessionResume.Plan(info, new NameStore());

        if (plan.Refusal is { Length: > 0 } refusal)
        {
            LinkDialog.Notice("Nothing to reopen.", refusal);
            return;
        }

        // Already up. Raising its terminal is the honest answer to "open this again" — and
        // the only visible one, since starting a second `claude --resume` on it would be a
        // duplicate of a session the operator already has.
        if (plan.AlreadyLive is { } running)
        {
            if (!SessionWindows.TryFocus(running.Pid))
                LinkDialog.Notice(
                    $"“{info.Name ?? info.SessionId}” is already open.",
                    $"It is running as pid {running.Pid}, but its terminal could not be brought forward.");
            return;
        }

        TerminalLauncher.Start(plan.Command!);
    }

    // --- done ---------------------------------------------------------------

    private static void Done(string idOrPrefix)
    {
        if (Find(idOrPrefix) is not { } info) return;

        var store = new DispositionStore();
        var before = store.Get(info.SessionId);
        store.SetMany([info.SessionId], Disposition.Done);

        var name = info.Name ?? info.SessionId;
        var headline = before == Disposition.Done
            ? $"“{name}” was already ticked off."
            : $"Ticked off “{name}”.";

        // The window already up is the best receipt there is: it re-reads the marks within
        // seconds and the card comes back struck through. Asking it to come forward is what
        // the single-instance handle is for, and it is the one thing that channel can carry.
        if (RaiseTheWindow())
        {
            if (store.LoadWarning is { Length: > 0 } warning)
                LinkDialog.Notice(headline, warning + ".");
            return;
        }

        LinkDialog.Notice(headline, store.LoadWarning is { Length: > 0 } w
            ? $"{w}."
            : "Sky is not running, so nothing on screen changed. It will show as done when you next open it.");
    }

    /// <summary>
    /// Ask the running app to show itself. False when there is none — the handle exists only
    /// while an instance holds it, so opening it is also how we find out.
    /// </summary>
    private static bool RaiseTheWindow()
    {
        if (!EventWaitHandle.TryOpenExisting(SingleInstance.ActivateName, out var activate))
            return false;

        using (activate) activate.Set();
        return true;
    }

    // --- new ----------------------------------------------------------------

    private static void New(string folder)
    {
        // The one verb that asks. A resume reopens something already yours and a done writes
        // a mark that `undone` reverses; this starts an agent in a folder, which is the only
        // one of the three worth a click to stop.
        if (!LinkDialog.Confirm(
                "Start a session here?",
                folder + "\n\nA new Claude Code session opens in a terminal, on Remote Control.",
                "Start session"))
            return;

        TerminalLauncher.Start(LaunchLine.NewIn(folder));
    }

    // --- shared -------------------------------------------------------------

    /// <summary>
    /// The session a link names. Ambiguity and absence are both refusals with a face, because
    /// the person who clicked cannot see what the id matched and has no other way to be told.
    /// </summary>
    private static SessionInfo? Find(string idOrPrefix)
    {
        var scanner = new SessionScanner();
        if (!scanner.ProjectsDirExists)
        {
            LinkDialog.Notice(
                "No sessions to look in.",
                $"There is no Claude Code projects folder at {scanner.ProjectsDir}.");
            return null;
        }

        var matches = scanner.FindByPrefix(idOrPrefix);

        if (matches.Count == 0)
        {
            LinkDialog.Notice("No such session.", $"Nothing here matches {idOrPrefix}.");
            return null;
        }

        if (matches.Count > 1)
        {
            LinkDialog.Notice(
                "That link names more than one session.",
                $"{idOrPrefix} matches {matches.Count} of them, so nothing was done. "
                + "A link needs enough of the id to mean one session.");
            return null;
        }

        return scanner.BuildRow(matches[0], SessionFileParser.DefaultContextWindow);
    }
}
