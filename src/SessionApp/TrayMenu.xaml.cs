using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SessionApp;

/// <summary>
/// The tray icon's right-click menu, as a small window rather than a WPF
/// <see cref="System.Windows.Controls.ContextMenu"/>.
///
/// The obvious build — a ContextMenu opened on the right-click message — does not survive
/// contact with the notification area. A ContextMenu is a Popup, and a Popup takes a mouse
/// capture as it opens; the shell still holds that capture at the moment it forwards the
/// click, so the capture fails and the menu closes itself in the same breath it was opened
/// (measured: <c>IsOpen</c> reads false on the line after it is set, even with the
/// foreground successfully taken). A window has no such dependency: it appears because it
/// was shown, and it goes away when it loses focus, which is the behaviour a menu wants
/// anyway.
///
/// Themed like the rest of the app, because a tray menu opens over the taskbar — the most
/// conspicuous possible place for a white slab in dark mode.
/// </summary>
public partial class TrayMenu : Window
{
    private readonly Action _open;
    private bool _closing;
    private bool _armed;

    private TrayMenu(Action open)
    {
        InitializeComponent();
        _open = open;
        // No ThemeManager.Attach: this window has no title bar and no icon to swap, and the
        // palette reaches it through the app-level dictionary like everything else.
    }

    /// <summary>Pops the menu at the mouse, kept inside the work area.</summary>
    public static void ShowAtCursor(Action open)
    {
        var menu = new TrayMenu(open);

        // Shown first, because its size is only known once it has measured, and its screen
        // position has to be worked out from that size. Invisible until placed, or it would
        // flash at wherever the last window happened to sit — and unactivated (see the XAML),
        // because a window that grabs focus on the way up gets it taken straight back by the
        // taskbar finishing with the click, and that bounce reads as "focus lost, close".
        menu.Opacity = 0;
        menu.Show();

        GetCursorPos(out POINT cursor);
        var at = menu.ToDeviceIndependent(cursor);
        var area = SystemParameters.WorkArea;

        // Up and to the left of the cursor: the taskbar is the one edge a tray menu is
        // always against, whichever side of the screen it is on.
        menu.Left = Math.Clamp(at.X - menu.ActualWidth, area.Left, Math.Max(area.Left, area.Right - menu.ActualWidth));
        menu.Top = Math.Clamp(at.Y - menu.ActualHeight, area.Top, Math.Max(area.Top, area.Bottom - menu.ActualHeight));

        menu.Opacity = 1;
        menu.Activate();   // now, in its final place, ask for the focus it will close on losing
    }

    // Closing on lost focus only counts once focus has actually been held. Without this the
    // menu closes itself on the way up, in the moment between being shown and being given
    // the foreground — which is why it must never close on a deactivation it never earned.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _armed = true;
    }

    /// <summary>
    /// Physical pixels (what the cursor is measured in) to the DIPs Left/Top expect. The
    /// window is already realised, so this is its own monitor's scaling, not the primary's.
    /// </summary>
    private Point ToDeviceIndependent(POINT pixels)
    {
        var source = PresentationSource.FromVisual(this) as HwndSource;
        var transform = source?.CompositionTarget?.TransformFromDevice;
        var point = new Point(pixels.X, pixels.Y);
        return transform is { } t ? t.Transform(point) : point;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _open();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Quit();
    }

    // Clicking anywhere else, or Esc: the menu is done. Nothing on it is destructive, so it
    // never asks twice.
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_armed) Dismiss();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Dismiss();
        e.Handled = true;
    }

    /// <summary>
    /// Close once. Picking an item closes the menu, and closing it deactivates it, so the
    /// deactivation arrives while the close is still in flight — and WPF throws on a Close
    /// during a Close. The flag is what keeps the two paths from meeting.
    /// </summary>
    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
