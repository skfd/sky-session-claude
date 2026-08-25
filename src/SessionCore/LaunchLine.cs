namespace SessionCore;

/// <summary>
/// The shell line that opens a session in a folder: into the folder first, because the
/// terminal starts wherever it likes, then Claude.
///
/// Separate from <see cref="ClaudeLaunch"/>, which composes the <c>claude</c> command alone
/// and knows nothing about shells. This is the pair of them, and it lives here rather than
/// in either front end because both of them open terminals — <c>SessionCli new</c> and the
/// app when a <c>skysession://new</c> link is clicked — and a folder that is quoted in one
/// place and not the other is a bug nobody finds until a path has an apostrophe in it.
/// </summary>
public static class LaunchLine
{
    public static string NewIn(string folder, string? name = null) =>
        $"cd {SessionName.Quote(folder)}; {ClaudeLaunch.New(name)}";
}
