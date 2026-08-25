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
