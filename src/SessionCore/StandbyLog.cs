namespace SessionCore;

/// <summary>
/// A line per thing standby did or decided not to do, in <c>standby.log</c> beside Sky's other
/// files.
///
/// The keeper works while nobody is watching, and a host's death leaves nothing behind but a
/// tab — so "the phone had nothing this morning" was a question with no record to answer it.
/// This is that record: when a host was found gone, when it was put back and how, and when the
/// keeper held off and why. It is a few lines a day on a quiet machine, and the oldest half is
/// dropped once it passes a quarter of a megabyte.
/// </summary>
public static class StandbyLog
{
    private const long Limit = 256 * 1024;
    private static readonly object Gate = new();

    public static string Path { get; } =
        System.IO.Path.Combine(DispositionStore.DefaultDir(), "standby.log");

    /// <summary>Append a line. Never throws: a log that cannot be written is not worth a failure.</summary>
    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > Limit)
                {
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Disk full, folder locked — the keeper carries on regardless.
        }
    }
}
