using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// The guard at the one point a title is resolved.
///
/// Naming writes into the transcript: a placeholder Sky passed under --name lands in the same
/// custom-title field a real title does. Taken at face value it would be shown as a title,
/// composed into a name, written again and read again — permanent by having been used once.
/// Refusing it here fixes the display, the launch paths and the policy together.
/// </summary>
public class RealTitleTests
{
    private const string Id = "b9e83ad3-8742-4f86-b5e3-40e844f24da1";
    private const string Cwd = @"C:\Users\kk\Code\ontario-address-changes";

    [Fact]
    public void ACustomTitleOutranksTheModelsOne() =>
        Assert.Equal("Fix the importer",
            SessionName.RealTitle("Fix the importer", "Start Chrome", Id, Cwd));

    [Fact]
    public void TheModelsTitleStandsWhenThereIsNoOther() =>
        Assert.Equal("Start Chrome", SessionName.RealTitle(null, "Start Chrome", Id, Cwd));

    /// <summary>The self-reinforcing loop, cut at the point it would have restarted.</summary>
    [Fact]
    public void APlaceholderInTheCustomTitleIsNotATitle() =>
        Assert.Equal("Start Chrome",
            SessionName.RealTitle("ontario-address-changes-b9", "Start Chrome", Id, Cwd));

    [Fact]
    public void APlaceholderWithNothingBehindItIsNoTitle() =>
        Assert.Null(SessionName.RealTitle("ontario-address-changes-b9", null, Id, Cwd));

    [Fact]
    public void APlaceholderInEitherFieldIsRefused() =>
        Assert.Null(SessionName.RealTitle("ontario-address-changes-b9", "ontario-address-changes-6c", Id, Cwd));

    [Fact]
    public void NoTitlesAtAllIsNoTitle() =>
        Assert.Null(SessionName.RealTitle(null, null, Id, Cwd));

    // --- and the same thing, through the scanner -------------------------------

    /// <summary>
    /// The plan's easy miss: a fix that lives only in the policy still leaves the app
    /// displaying "ontario-address-changes-b9" as a title, and NamedCommand still rewriting
    /// it on every restart.
    /// </summary>
    [Fact]
    public void TheScannerDoesNotResolveAPlaceholderAsATitle()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}");
        var project = Path.Combine(dir, "C--Users-kk-Code-ontario-address-changes");
        Directory.CreateDirectory(project);
        try
        {
            var file = Path.Combine(project, $"{Id}.jsonl");
            File.WriteAllLines(file,
            [
                $$"""{"type":"ai-title","aiTitle":"Add retry logic","cwd":"{{Cwd.Replace(@"\", @"\\")}}"}""",
                """{"type":"custom-title","customTitle":"ontario-address-changes-b9"}""",
            ]);

            var info = new SessionScanner(dir).BuildRow(new FileInfo(file), 200_000);

            Assert.Equal("Add retry logic", info.Name);
            Assert.Equal("ontario-address-changes-b9", info.CustomTitle);
            Assert.Equal("Add retry logic", info.AiTitle);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
