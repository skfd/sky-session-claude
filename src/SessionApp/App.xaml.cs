using System.Windows;

namespace SessionApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the instance slot, and well before any main window: this launch is not a
        // request to see the app at all. Windows started it because someone clicked a
        // skysession:// link, and it exists to do that one thing and go. Claiming the slot
        // would take it from the window already up; letting base.OnStartup run would build
        // a second one.
        if (LinkHandler.UrlIn(e.Args) is { } url)
        {
            // The dialogs the handler may show resolve brushes against the palette, so it
            // has to be in the resources first -- same reason the ordinary path does it
            // before base.OnStartup.
            ThemeManager.Initialize();

            // A handler that shows nothing must still end the process: with no main window
            // and OnLastWindowClose, a launch that only opens a terminal would otherwise
            // leave a windowless app running forever.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try { LinkHandler.Handle(url); }
            finally { Shutdown(); }
            return;
        }

        // Before anything builds a window: a second launch has nothing to show, and the
        // instance already up is being asked to come forward instead (see SingleInstance).
        _instance = SingleInstance.Claim(
            allowMultiple: e.Args.Any(a => string.Equals(a, "--multi", StringComparison.OrdinalIgnoreCase)));

        if (!_instance.IsFirst)
        {
            Shutdown();
            return;
        }

        // Ahead of base.OnStartup, which builds the StartupUri window: the palette has
        // to be in the resources before anything resolves a brush against it.
        ThemeManager.Initialize();
        base.OnStartup(e);

        _instance.OnActivateRequested(() => Dispatcher.Invoke(ShowMainWindow));
    }

    /// <summary>Bring the one window forward, however it was left — minimised, or behind.</summary>
    private void ShowMainWindow()
    {
        if (MainWindow is not { } window) return;

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
