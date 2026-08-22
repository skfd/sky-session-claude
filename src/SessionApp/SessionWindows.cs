using System.Runtime.InteropServices;
using System.Windows.Automation;
using SessionCore;

namespace SessionApp;

/// <summary>
/// Brings the terminal showing a live session to the front, so a double-click can jump to
/// that window instead of spawning a second <c>claude --resume</c> against the same session.
///
/// This is the half of live-session handling that needs a desktop: UI Automation to find a
/// tab, and the foreground dance to raise a window. Finding the sessions themselves, and
/// the shell behind one, is <see cref="LiveSessions"/> in the core — headless, and shared
/// with the CLI.
///
/// Windows Terminal is one process behind every window and tab, so the process tree stops
/// at "one of these nine windows" — and each pane's shell hangs directly off
/// <c>WindowsTerminal.exe</c>, with that pane's <c>OpenConsole.exe</c> a sibling rather
/// than its parent, so no tree edge names the tab either. The title closes that last gap:
/// attaching to the session's console reads back the title the CLI painted, and UI
/// Automation finds the tab wearing it.
/// </summary>
internal static class SessionWindows
{
    /// <summary>
    /// Bring the terminal showing <paramref name="pid"/> to the foreground, switching to its
    /// tab when the host is tabbed. Returns false if no visible host window can be resolved
    /// (the caller should then fall back to opening a fresh terminal).
    /// </summary>
    public static bool TryFocus(int pid)
    {
        var target = ResolveTarget(pid);
        if (target.Hwnd == IntPtr.Zero) return false;

        TrySelect(target.Tab);          // no-op unless the host has tabs
        return Activate(target.Hwnd);
    }

    // --- window resolution --------------------------------------------------

    /// <summary>The window to raise, and the tab inside it to switch to (if any).</summary>
    private readonly record struct FocusTarget(IntPtr Hwnd, AutomationElement? Tab);

    // The terminal window that shows a console app is owned either by an ancestor
    // (Windows Terminal hosts the shell several levels up) or by a conhost/OpenConsole
    // child of the shell (the classic console window). So the candidate owners are the
    // process's ancestors plus each ancestor's console-host children; the first that owns
    // any visible top-level window is the host.
    //
    // Under Windows Terminal that one host owns every window at once, so "the first
    // window" is a coin toss — the title is what picks the right one out.
    private static FocusTarget ResolveTarget(int pid)
    {
        var (parents, childrenOf) = ProcessTree.Snapshot();
        var windows = TopLevelWindowsByPid();

        var candidates = new List<int>();
        var seen = new HashSet<int>();
        int cur = pid;
        for (int depth = 0; depth < 16 && cur != 0 && seen.Add(cur); depth++)
        {
            candidates.Add(cur);
            if (childrenOf.TryGetValue(cur, out var kids))
                foreach (var kid in kids)
                    if (IsConsoleHost(kid.Name)) candidates.Add(kid.Pid);

            cur = parents.TryGetValue(cur, out var parent) ? parent : 0;
        }

        foreach (var candidate in candidates)
            if (windows.TryGetValue(candidate, out var hwnds) && hwnds.Count > 0)
                return PickTab(hwnds, ConsoleInput.ReadTitle(pid));

        return default;
    }

    // Find the window and tab wearing this session's title. Falling back to the first
    // window keeps the old behaviour whenever the title is unreadable or ambiguous: the
    // wrong tab of the right window still beats spawning a duplicate session.
    private static FocusTarget PickTab(List<IntPtr> hwnds, string title)
    {
        if (TerminalTitle.Topic(title).Length > 0)
            foreach (var hwnd in hwnds)
                if (MatchingTab(hwnd, title) is { } tab)
                    return new FocusTarget(hwnd, tab);

        return new FocusTarget(hwnds[0], null);
    }

    // The one tab of this window that names the session — null if none does, or if several
    // do (two idle sessions both sit under the title "Claude Code", and guessing between
    // them would drag the user somewhere they did not ask to go).
    private static AutomationElement? MatchingTab(IntPtr hwnd, string title)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            if (window is null) return null;

            var tabs = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

            AutomationElement? found = null;
            foreach (AutomationElement tab in tabs)
            {
                if (!TerminalTitle.SameSession(tab.Current.Name, title)) continue;
                if (found is not null) return null;      // ambiguous
                found = tab;
            }
            return found;
        }
        catch { return null; }   // window closed mid-walk, or it exposes no automation tree
    }

    private static void TrySelect(AutomationElement? tab)
    {
        if (tab is null) return;
        try
        {
            if (true.Equals(tab.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty))) return;
            if (tab.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern pattern)
                pattern.Select();
        }
        catch { /* tab closed, or the host cannot select — raise the window regardless */ }
    }

    private static bool IsConsoleHost(string name) =>
        name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase);

    // --- foreground activation (the documented AttachThreadInput dance) ------

    private static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint appThread = GetCurrentThreadId();

        bool attached = fgThread != appThread && AttachThreadInput(fgThread, appThread, true);
        try
        {
            BringWindowToTop(hwnd);
            bool ok = SetForegroundWindow(hwnd);
            return ok || GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, appThread, false);
        }
    }

    // --- window enumeration -------------------------------------------------

    // Every visible, titled, top-level window, grouped by owning pid — all of them,
    // because one Windows Terminal process owns all of its windows and only the tab titles
    // say which is which.
    private static Dictionary<int, List<IntPtr>> TopLevelWindowsByPid()
    {
        var byPid = new Dictionary<int, List<IntPtr>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;   // owned pop-up, not a main window
            if (GetWindowTextLength(hwnd) == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            if (!byPid.TryGetValue((int)pid, out var list)) byPid[(int)pid] = list = new List<IntPtr>();
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return byPid;
    }

    // --- P/Invoke -----------------------------------------------------------

    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
}
