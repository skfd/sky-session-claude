using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks the rule that pairs a live session with its terminal tab. Double-click has to
/// land on the tab actually running the session, and the only string linking the two is
/// the title — which carries a status glyph that changes under us mid-jump.
/// </summary>
public class TerminalTitleTests
{
    [Theory]
    [InlineData("✳ Claude Code", "Claude Code")]
    [InlineData("◐ Ongoing work", "Ongoing work")]
    [InlineData("Windows PowerShell", "Windows PowerShell")]
    [InlineData("  ✳   Release versioning strategy  ", "Release versioning strategy")]
    public void Topic_DropsTheStatusGlyph(string title, string expected) =>
        Assert.Equal(expected, TerminalTitle.Topic(title));

    [Fact]
    public void Topic_OfNothingButAGlyph_IsEmpty()
    {
        Assert.Equal("", TerminalTitle.Topic("✳"));
        Assert.Equal("", TerminalTitle.Topic("   "));
        Assert.Equal("", TerminalTitle.Topic(null));
    }

    /// <summary>
    /// The session flips busy → idle between reading the process's console title and
    /// reading the window's tab titles; the glyph differs, the tab is still the one.
    /// </summary>
    [Fact]
    public void SameSession_IgnoresAGlyphThatChangedMidJump() =>
        Assert.True(TerminalTitle.SameSession("◐ Multi-tab terminal detection",
                                              "✳ Multi-tab terminal detection"));

    [Fact]
    public void SameSession_SeparatesDifferentConversations() =>
        Assert.False(TerminalTitle.SameSession("✳ Release versioning strategy",
                                               "✳ Multi-tab terminal detection"));

    /// <summary>
    /// A glyph-only title would otherwise match every other glyph-only title, and we
    /// would switch the user to a tab at random. Matching nothing sends the caller back
    /// to plain window activation, which is merely unhelpful rather than wrong.
    /// </summary>
    [Fact]
    public void SameSession_MatchesNothingWhenThereIsNoTopic() =>
        Assert.False(TerminalTitle.SameSession("✳", "◐"));
}
