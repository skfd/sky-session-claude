using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SessionApp;

public enum AppTheme { Light, Dark }

/// <summary>
/// Follows the Windows "app mode" setting: picks light or dark at startup and swaps
/// the palette live when the user flips the system toggle, so the app never sits in
/// the wrong mode until the next launch.
///
/// The palette lives in its own merged dictionary at slot 0 of the app resources;
/// swapping that one dictionary repaints everything, because every consumer reaches
/// the brushes through DynamicResource. The non-client area (title bar) is not ours
/// to style, so it is handed to DWM separately.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static ResourceDictionary? _palette;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Applies the current system theme. Call before the first window is built.</summary>
    public static void Initialize() => Apply(ReadSystemTheme(), force: true);

    /// <summary>
    /// Themes a window's title bar and starts listening for system theme changes on it.
    /// Safe to call before the window is shown; the hook waits for the HWND.
    /// </summary>
    public static void Attach(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook(window);
        else window.SourceInitialized += (_, _) => Hook(window);
    }

    private static void Hook(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ApplyTitleBar(hwnd, Current);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    // Windows announces a light/dark switch as WM_SETTINGCHANGE("ImmersiveColorSet");
    // the registry holds the actual value, so re-read it rather than trusting the ping.
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE
            && lParam != IntPtr.Zero
            && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Apply(ReadSystemTheme(), force: false);
        }
        return IntPtr.Zero;
    }

    private static void Apply(AppTheme theme, bool force)
    {
        if (!force && theme == Current) return;
        Current = theme;

        var app = Application.Current;
        if (app is null) return;

        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Theme/{theme}.xaml", UriKind.Absolute),
        };

        var dictionaries = app.Resources.MergedDictionaries;
        if (_palette is not null) dictionaries.Remove(_palette);
        dictionaries.Insert(0, next);
        _palette = next;

        foreach (Window w in app.Windows)
            ApplyTitleBar(new WindowInteropHelper(w).Handle, theme);
    }

    private static AppTheme ReadSystemTheme()
    {
        // Missing value (or a locked-down policy hive) means the classic default: light.
        var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
        return value is int i && i == 0 ? AppTheme.Dark : AppTheme.Light;
    }

    private static void ApplyTitleBar(IntPtr hwnd, AppTheme theme)
    {
        if (hwnd == IntPtr.Zero) return;

        int dark = theme == AppTheme.Dark ? 1 : 0;
        // Pre-20H1 builds do not know the attribute and fail harmlessly.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0) return;

        // A live switch only reaches the frame once it is invalidated.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_FRAMECHANGED = 0x0020;
    private const int SWP_NOACTIVATE = 0x0010;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);
}
