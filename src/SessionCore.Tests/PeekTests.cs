using SessionCli;

namespace SessionCore.Tests;

/// <summary>
/// `peek` borrows a real console, so what it does cannot be tested without one. What can be
/// is the argument handling in front of that borrow — the part that decides whether anyone's
/// terminal is touched at all.
/// </summary>
public class PeekTests
{
    private static Args Parse(params string[] argv) => new("peek", argv);

    [Fact]
    public void NeedsExactlyOneSession()
    {
        Assert.Throws<UsageException>(() => Commands.Peek(Parse()));
        Assert.Throws<UsageException>(() => Commands.Peek(Parse("abc", "def")));
    }

    // It reads and never types, so it has no --dry-run to offer; a caller reaching for one
    // has misread the verb and should be told rather than quietly obeyed.
    [Fact]
    public void TakesNoFlags()
    {
        Assert.Throws<UsageException>(() => Commands.Peek(Parse("abc", "--dry-run")));
    }
}
