namespace SessionCore;

/// <summary>
/// Watches ~/.claude/projects for transcript changes and raises a single debounced
/// <see cref="Changed"/> event after activity settles. The event fires on a
/// thread-pool thread; subscribers touching UI must marshal to their own thread.
/// </summary>
public sealed class ProjectsWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly System.Threading.Timer _debounce;
    private readonly int _debounceMs;

    public event Action? Changed;

    public ProjectsWatcher(string projectsDir, int debounceMs = 800)
    {
        _debounceMs = debounceMs;
        _debounce = new System.Threading.Timer(_ => Changed?.Invoke(), null,
            Timeout.Infinite, Timeout.Infinite);

        _fsw = new FileSystemWatcher(projectsDir, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _fsw.Changed += OnAny;
        _fsw.Created += OnAny;
        _fsw.Deleted += OnAny;
        _fsw.Renamed += OnAny;
        _fsw.EnableRaisingEvents = true;
    }

    // Restart the debounce window on every filesystem event; Changed fires once it settles.
    private void OnAny(object sender, FileSystemEventArgs e) =>
        _debounce.Change(_debounceMs, Timeout.Infinite);

    public void Dispose()
    {
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
