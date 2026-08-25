using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SessionApp;

/// <summary>
/// Sky's icon in the notification area, next to the clock.
///
/// It rides the main window's own HWND rather than a message-only window of its own: the
/// window is never destroyed once hide-to-tray is in force, and a second HWND would be a
/// second thing to keep alive for no gain. The shell talks back through one private
/// message (<see cref="CallbackMessage"/>) carrying the mouse event in lParam.
///
/// Two things are easy to get wrong and both are handled here:
/// <list type="bullet">
/// <item>Explorer restarting broadcasts <c>TaskbarCreated</c> and forgets every icon. An
/// app that does not listen for it loses its icon for the rest of the session.</item>
/// <item>The icon must be removed on exit, or its ghost sits in the tray until something
/// makes the shell notice the owner is gone — usually the next time you hover over it.</item>
/// </list>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // WM_APP + 1: the range reserved for an application's own messages, so it can never
    // collide with anything WPF sends this window.
    private const int CallbackMessage = 0x8000 + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Window _host;

    private IntPtr _hwnd;
    private IntPtr _icon;          // owned: destroyed when replaced, and on the way out
    private bool _added;
    private int _count = -1;
    private string _tip = "";
    private bool _disposed;

    /// <summary>A plain left click on the icon.</summary>
    public event Action? Clicked;

    /// <summary>A right click — the caller decides what a menu is.</summary>
    public event Action? MenuRequested;

    public TrayIcon(Window host)
    {
        _host = host;

        // Same shape as ThemeManager.Attach: usable before the window is shown.
        if (new WindowInteropHelper(host).Handle != IntPtr.Zero) Hook();
        else host.SourceInitialized += (_, _) => Hook();
    }

    /// <summary>
    /// Sets what the icon says. Cheap to call on every scan: an unchanged count redraws
    /// nothing, which matters because the live poll runs every three seconds.
    /// </summary>
    public void Update(int count, string tip)
    {
        if (count == _count && tip == _tip) return;
        _count = count;
        _tip = tip;
        Push();
    }

    private void Hook()
    {
        _hwnd = new WindowInteropHelper(_host).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Push();
    }

    private void Push()
    {
        // _count below zero means no scan has finished yet. Better no icon for the second
        // that takes than an icon that says 0 and then corrects itself.
        if (_hwnd == IntPtr.Zero || _disposed || _count < 0) return;

        IntPtr fresh = CountIcon.Render(_count);
        var data = Describe(fresh, _tip);

        if (!Shell_NotifyIcon(_added ? NIM_MODIFY : NIM_ADD, ref data))
        {
            // A failed NIM_MODIFY means the shell has no record of us — an Explorer restart
            // whose broadcast we missed, say. Re-adding is the recovery.
            _added = false;
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                if (fresh != IntPtr.Zero) CountIcon.DestroyIcon(fresh);
                return;
            }
        }

        _added = true;
        // Only now: the shell has taken the new one, so the old handle is free to go.
        if (_icon != IntPtr.Zero) CountIcon.DestroyIcon(_icon);
        _icon = fresh;
    }

    private NOTIFYICONDATA Describe(IntPtr icon, string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip,
        // Fixed-length string fields: the marshaller wants a value, not a null, even for
        // the balloon parts this icon never uses.
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreated)
        {
            _added = false;
            Push();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage) return IntPtr.Zero;

        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
                Clicked?.Invoke();
                handled = true;
                break;
            case WM_RBUTTONUP:
                // Foreground first. A tray click is one of the moments Windows lets the
                // icon's owner take the foreground, and the menu wants it: a window that
                // never became active never goes inactive, and would sit there unclosable.
                SetForegroundWindow(_hwnd);
                MenuRequested?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = Describe(IntPtr.Zero, "");
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            CountIcon.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
