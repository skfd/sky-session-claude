namespace SessionCore.Tests;

/// <summary>
/// The screens here are real: read off a terminal with <see cref="ConsoleInput.ReadScreen"/>
/// while a session sat on Claude Code's trust prompt, and pasted back verbatim.
///
/// What is being tested is the decision to press Enter into someone else's terminal, and the
/// case that matters most is the one where it must not: the same keystroke that trusts the
/// folder closes the session when the selection has moved to "No, exit".
/// </summary>
public class TrustPromptTests
{
    private const string Showing = """

        ────────────────────────────────────────────────────────────────────
         Accessing workspace:

         C:\Users\kk\Code\DynamicsCrmEasyMigration

         Quick safety check: Is this a project you created or one you trust?
         (Like your own code, a well-known open source project, or work from
         your team). If not, take a moment to review what's in this folder.

         Claude Code'll be able to read, edit, and execute files here.

         Security guide

         ❯ 1. Yes, I trust this folder
           2. No, exit

         Enter to confirm · Esc to cancel
        """;

    // The same dialog after one press of the down arrow.
    private const string MovedOff = """

         Security guide

           1. Yes, I trust this folder
         ❯ 2. No, exit

         Enter to confirm · Esc to cancel
        """;

    private const string RunningSession = """

        ╭─── Claude Code v2.1.240 ──────────────────────────────────────────╮
        │ Tips for getting started                                          │
        ╰───────────────────────────────────────────────────────────────────╯
        ❯ Try "create a util logging.py that..."
          Opus 5 (1M context) │ DynamicsCrmEasyMigration  master │ 0%
        """;

    [Fact]
    public void ReadsTheDialogWithYesSelected()
    {
        Assert.Equal(TrustPrompt.State.YesSelected, TrustPrompt.Read(Showing));
    }

    // The one that keeps a session alive: Enter here takes "No, exit".
    [Fact]
    public void RefusesWhenTheSelectionHasMovedOffYes()
    {
        Assert.Equal(TrustPrompt.State.OtherSelected, TrustPrompt.Read(MovedOff));
    }

    [Fact]
    public void SeesNoDialogInASessionThatIsSimplyRunning()
    {
        Assert.Equal(TrustPrompt.State.NotShowing, TrustPrompt.Read(RunningSession));
    }

    // A console that could not be borrowed comes back empty, which is not a dialog.
    [Fact]
    public void SeesNoDialogInAnEmptyScreen()
    {
        Assert.Equal(TrustPrompt.State.NotShowing, TrustPrompt.Read(""));
    }

    // A selection marker elsewhere on the screen is not this dialog's; only the marker on
    // the line carrying the option counts.
    [Fact]
    public void IgnoresASelectionMarkerOnAnotherLine()
    {
        var elsewhere = "❯ something else\n  1. Yes, I trust this folder\n  2. No, exit";
        Assert.Equal(TrustPrompt.State.OtherSelected, TrustPrompt.Read(elsewhere));
    }

    [Fact]
    public void MatchesTheFolderTheDialogNames()
    {
        Assert.True(TrustPrompt.IsAbout(Showing, @"C:\Users\kk\Code\DynamicsCrmEasyMigration"));
        Assert.True(TrustPrompt.IsAbout(Showing, @"C:\Users\kk\Code\DynamicsCrmEasyMigration\"));
    }

    // The point of the folder check: a launch in one repo must not answer a prompt that
    // happens to be open in another.
    [Fact]
    public void DoesNotMatchADifferentFolder()
    {
        Assert.False(TrustPrompt.IsAbout(Showing, @"C:\Users\kk\Code\sky-session-claude"));
    }

    // A path too long for the box is wrapped or clipped, so the leaf still counts.
    [Fact]
    public void MatchesOnTheFolderNameWhenThePathIsClipped()
    {
        var clipped = " Accessing workspace:\n …\\Code\\DynamicsCrmEasyMigration\n ❯ 1. Yes, I trust this folder";
        Assert.True(TrustPrompt.IsAbout(clipped, @"D:\somewhere\else\DynamicsCrmEasyMigration"));
    }
}
