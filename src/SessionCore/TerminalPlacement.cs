using System.Runtime.InteropServices;

namespace SessionCore;

/// <summary>
/// A screen's working area — what the taskbar leaves of it — in desktop pixels, and whether it
/// is the primary one.
/// </summary>
public readonly record struct ScreenArea(int Left, int Top, int Right, int Bottom, bool Primary)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Where a new terminal window goes: the second screen, because that is where the operator
/// keeps every terminal. Left to Windows, a window opens on the primary screen — the one with
/// the app, the browser and the editor on it — and gets dragged across by hand, one window per
/// session started, a standby run's window included.
///
/// "The second screen" is a non-primary one. With several, it is whichever already holds the
/// most terminal windows, so the rule follows where the operator actually put them rather than
/// a guess at which panel is the laptop's; a tie goes to the first Windows lists. With one
/// screen there is nowhere else to go, and the answer is nothing — the window opens wherever
/// Windows puts it, as it always has.
///
/// Windows already on that screen push the next one down and right by a step, the way Windows'
/// own cascade does, so five sessions started in a row are five windows and not one window
/// with four more exactly underneath it.
/// </summary>
public static class TerminalPlacement
{
    /// <summary>How far each window sits from the one before it, as Windows' own cascade does.</summary>
    public const int Step = 26;

    /// <summary>How many steps the cascade takes before it starts again at the top.</summary>
    public const int Cascade = 8;

    /// <summary>
    /// The top-left corner for a new terminal window as <c>wt --pos</c> takes it, <c>x,y</c>,
    /// or null when there is no second screen or the screens will not say.
    /// </summary>
    public static string? Position()
    {
        try
        {
            return WithPhysicalPixels(() => Choose(Screens(), TerminalCentres())) is { } at
                ? $"{at.X},{at.Y}"
                : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;   // a launch that lands on the wrong screen beats one that does not happen
        }
    }

    /// <summary>
    /// The decision on its own, for the tests: the corner on the chosen screen, given every
    /// screen and the centre of every terminal window already open.
    ///
    /// Centres rather than corners because a window's rectangle includes a border Windows
    /// draws invisibly outside it: a window flush with a screen's left edge has its corner a
    /// few pixels onto the next screen over.
    /// </summary>
    public static (int X, int Y)? Choose(IReadOnlyList<ScreenArea> screens, IReadOnlyList<(int X, int Y)> terminals)
    {
        int On(ScreenArea screen) => terminals.Count(t => screen.Contains(t.X, t.Y));

        var others = screens.Where(s => !s.Primary).ToList();
        if (others.Count == 0) return null;

        // OrderByDescending is stable, so a tie keeps the order Windows listed the screens in.
        var target = others.OrderByDescending(On).First();
        var step = Step * (1 + On(target) % Cascade);
        return (target.Left + step, target.Top + step);
    }

    private static List<ScreenArea> Screens()
    {
        var found = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                found.Add(new ScreenArea(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom,
                    (info.dwFlags & MonitorPrimary) != 0));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The centre of every terminal window on the desktop: Windows Terminal's, and the old
    /// console's for a machine still on it. A minimised window is parked far off every screen
    /// and so counts for none of them.
    /// </summary>
    private static List<(int X, int Y)> TerminalCentres()
    {
        var found = new List<(int X, int Y)>();
        var name = new char[64];
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var length = GetClassName(window, name, name.Length);
            var cls = new string(name, 0, Math.Max(length, 0));
            if (cls is "CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass" && GetWindowRect(window, out var r))
                found.Add(((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Ask in real pixels. Windows Terminal reads <c>--pos</c> in them, and a process that has
    /// not declared itself DPI-aware — the CLI — is otherwise told a scaled-down desktop on a
    /// screen set above 100%, and would aim short.
    /// </summary>
    private static T WithPhysicalPixels<T>(Func<T> ask)
    {
        IntPtr before;
        try { before = SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
        catch (EntryPointNotFoundException) { return ask(); }   // older than Windows 10 1607

        try { return ask(); }
        finally { if (before != IntPtr.Zero) SetThreadDpiAwarenessContext(before); }
    }

    private const uint MonitorPrimary = 1;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
