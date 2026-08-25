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

        // Windows starts Sky at sign-in with --tray (publish.ps1 writes the Run entry). The
        // count belongs by the clock from the moment you log in, but a window in front of
        // everything else that opens then is not what was asked for, so this launch ends up
        // exactly where a click on the X leaves Sky: running, counting, out of the way.
        bool tray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        // Before anything builds a window: a second launch has nothing to show, and the
        // instance already up is being asked to come forward instead (see SingleInstance).
        // A tray start is the exception — it is Windows opening Sky rather than you, so
        // finding a window already up it leaves quietly instead of yanking that one forward.
        _instance = SingleInstance.Claim(
            allowMultiple: e.Args.Any(a => string.Equals(a, "--multi", StringComparison.OrdinalIgnoreCase)),
            activateExisting: !tray);

        if (!_instance.IsFirst)
        {
            Shutdown();
            return;
        }

        // Ahead of the window below, which resolves brushes against the palette as it builds.
        ThemeManager.Initialize();
        base.OnStartup(e);

        // The window is built either way, because the tray icon rides its HWND (see TrayIcon)
        // and there is no count by the clock without one. A tray start creates the handle and
        // stops there; anything else puts it on screen. This is why App.xaml no longer names a
        // StartupUri: that shows the window it builds, and one of these two must not.
        var window = new MainWindow();
        MainWindow = window;
        if (tray) window.StartHidden();
        else window.Show();

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
