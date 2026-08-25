using System.Windows;

namespace SessionApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>
    /// True once the app is actually on its way out, as opposed to the window being closed
    /// — which now only hides it. Every real exit goes through <see cref="Quit"/> or a
    /// Windows sign-out, and the window reads this to know which one it is looking at.
    /// </summary>
    public static bool Exiting { get; private set; }

    /// <summary>Leave for good: the tray menu's Exit, and the only way there is one.</summary>
    public static void Quit()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Two launches that are not requests to see the app, both of them settled before the
        // instance slot and well before any main window. --quit goes first because it is a
        // message rather than work: publish.ps1 sends it to clear the exe it is about to
        // overwrite, and it must not be delayed behind anything a link might put on screen.
        if (e.Args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase)))
        {
            SingleInstance.RequestQuit();
            Shutdown();
            return;
        }

        // Windows started this one because someone clicked a skysession:// link, and it
        // exists to do that one thing and go. Claiming the slot would take it from the
        // window already up; letting base.OnStartup run would build a second one.
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
        _instance.OnQuitRequested(() => Dispatcher.Invoke(Quit));

        // Signing out or shutting down closes windows the same way a click on the X does,
        // and a window that answers that by hiding would sit there refusing to go.
        SessionEnding += (_, _) => Exiting = true;
    }

    /// <summary>
    /// Bring the one window forward, however it was left — minimised, behind, or hidden in
    /// the tray. Shared by the tray icon and by a second launch asking to see the window.
    /// </summary>
    public void ShowMainWindow()
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
