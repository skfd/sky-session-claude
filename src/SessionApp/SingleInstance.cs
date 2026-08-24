using System.Threading;

namespace SessionApp;

/// <summary>
/// One Sky window per desktop.
///
/// The app is a view onto shared state — the registry, the projects folder, the
/// disposition store — and a second window is not a second workspace, it is the same one
/// twice. Two of them disagree the moment either acts: two polls both decide a session
/// needs a name, two renames land, each instance re-reads the other's answer and writes
/// again. Renaming is the first thing Sky does unasked, so the cheapest way to keep it
/// honest is for there to be one of it.
///
/// A second launch is a request to see the window, not to have another. So it signals the
/// instance that is already up and leaves; the running window comes forward as if the
/// shortcut had focused it, which is what was wanted.
///
/// <c>--multi</c> opts out. A dev build alongside the stable one is the case that needs it:
/// <c>publish.ps1</c> closes only the installed exe and leaves the dev build running, and
/// working on the app while watching it run is worth an escape hatch.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Local\ rather than Global\: the guard is per logged-in desktop, so two people on one
    // machine each get their own window rather than one silently stealing the other's.
    private const string MutexName = @"Local\sky-session-claude-app";
    private const string ActivateName = @"Local\sky-session-claude-activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;

    public bool IsFirst { get; }

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate, bool isFirst)
    {
        _mutex = mutex;
        _activate = activate;
        IsFirst = isFirst;
    }

    /// <summary>
    /// Claim the single-instance slot. <see cref="IsFirst"/> is false when another window
    /// already holds it, having first asked that one to come forward.
    /// </summary>
    public static SingleInstance Claim(bool allowMultiple)
    {
        if (allowMultiple) return new SingleInstance(null, null, isFirst: true);

        // createdNew is the whole answer: whoever creates the mutex owns the slot. An
        // instance that crashed without disposing leaves an abandoned mutex, which the
        // next launch acquires normally — the slot frees itself.
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);

        if (createdNew) return new SingleInstance(mutex, activate, isFirst: true);

        activate.Set();          // ask the window that is already up to show itself
        mutex.Dispose();
        activate.Dispose();
        return new SingleInstance(null, null, isFirst: false);
    }

    /// <summary>
    /// Run <paramref name="show"/> whenever another launch asks for the window. The
    /// callback arrives on a pool thread, so the caller marshals to the UI itself.
    /// </summary>
    public void OnActivateRequested(Action show)
    {
        if (_activate is null) return;

        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => show(), state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();

        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never acquired */ }
        _mutex.Dispose();
    }
}
