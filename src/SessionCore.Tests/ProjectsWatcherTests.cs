using SessionCore;

namespace SessionCore.Tests;

public class ProjectsWatcherTests
{
    [Fact]
    public async Task FiresDebouncedChanged_WhenSessionFileChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var sub = Directory.CreateDirectory(Path.Combine(dir, "project-a")).FullName;
        try
        {
            using var watcher = new ProjectsWatcher(dir, debounceMs: 200);
            var fired = new TaskCompletionSource();
            int count = 0;
            watcher.Changed += () => { Interlocked.Increment(ref count); fired.TrySetResult(); };

            // Create + append to a session file under a project subfolder.
            var file = Path.Combine(sub, "session.jsonl");
            await File.WriteAllTextAsync(file, "{\"type\":\"ai-title\",\"aiTitle\":\"x\"}\n");
            await File.AppendAllTextAsync(file, "{\"type\":\"last-prompt\",\"lastPrompt\":\"go\"}\n");

            var done = await Task.WhenAny(fired.Task, Task.Delay(3000));
            Assert.True(ReferenceEquals(done, fired.Task), "watcher did not fire within 3s");

            // Debounce should coalesce the burst into a single (or very few) events.
            await Task.Delay(400);
            Assert.True(count >= 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
