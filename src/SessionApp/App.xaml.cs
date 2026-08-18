using System.Windows;

namespace SessionApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Ahead of base.OnStartup, which builds the StartupUri window: the palette has
        // to be in the resources before anything resolves a brush against it.
        ThemeManager.Initialize();
        base.OnStartup(e);
    }
}
