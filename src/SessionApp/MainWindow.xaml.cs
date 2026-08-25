using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SessionCore;

namespace SessionApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private ProjectsWatcher? _watcher;
    private DispatcherTimer? _liveTimer;
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        ThemeManager.Attach(this);   // dark title bar + live system theme switching
        _vm.SelectionKeeper = KeepSelection;
        StartTray();
        Loaded += async (_, _) =>
        {
            await _vm.RefreshAsync();
            DrawTray();          // first real count: the icon appears now, not saying 0
            StartWatcher();
            StartLiveTimer();
        };
        // The X puts Sky in the tray instead of quitting it. Everything that keeps the
        // count honest — the watcher, the three-second poll, the parse cache — carries on
        // behind the hidden window, so the number by the clock stays true and coming back
        // costs nothing. Leaving for good is the tray menu's Exit, and App.Exiting is how
        // this tells that (and a Windows sign-out) apart from a click on the X.
        Closing += (_, e) =>
        {
            if (App.Exiting) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            _watcher?.Dispose();
            _liveTimer?.Stop();
            _tray?.Dispose();
        };
    }

    // Rescans and filter changes reset the collection view, and the ListBox drops its
    // selection on reset. Rows keep their identity across a merge, so re-select the same
    // ones afterwards, minus any the filter now hides.
    private void KeepSelection(Action update)
    {
        var selected = Grid.SelectedItems.OfType<SessionRow>().ToList();
        update();
        if (selected.Count == 0) return;

        var survivors = selected.Where(_vm.RowsView.Contains).ToList();
        if (survivors.Count == Grid.SelectedItems.Count
            && survivors.All(Grid.SelectedItems.Contains)) return;

        Grid.SelectedItems.Clear();
        foreach (var row in survivors) Grid.SelectedItems.Add(row);
    }

    // The icon by the clock. It lives on this window's HWND (see TrayIcon), so it is built
    // here rather than in App, and it shows the same number the title bar spells out.
    private void StartTray()
    {
        _tray = new TrayIcon(this);
        _tray.Clicked += ToggleFromTray;
        _tray.MenuRequested += () => TrayMenu.ShowAtCursor(((App)Application.Current).ShowMainWindow);

        // Every scan recomputes the count, so the icon follows the view model rather than
        // running a poll of its own.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UnfinishedCount)) DrawTray();
        };
    }

    private void DrawTray()
    {
        int n = _vm.UnfinishedCount;
        // The tooltip carries the exact figure, which matters above 99 where the glyph
        // gives up and says "99+".
        _tray?.Update(n, $"Sky Session Claude\n{n} session{(n == 1 ? "" : "s")} still on the hook");
    }

    // A left click on the icon toggles: up if Sky is away or minimised, back to the tray if
    // it is already up. Deliberately not "activate if not focused" — clicking the tray hands
    // the foreground to the taskbar first, so the window's own IsActive is false by the time
    // this runs and every click would read as "show".
    private void ToggleFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ((App)Application.Current).ShowMainWindow();
    }

    // Auto-refresh when a session file changes (debounced in ProjectsWatcher).
    private void StartWatcher()
    {
        var dir = SessionScanner.DefaultProjectsDir();
        if (!System.IO.Directory.Exists(dir)) return;

        _watcher = new ProjectsWatcher(dir);
        _watcher.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_vm.LiveUpdates && _vm.RefreshCommand.CanExecute(null))
                _vm.RefreshCommand.Execute(null);
        });
    }

    // Keep the "open in a terminal" dots honest. Polled rather than watched: opening a
    // session touches its file (so the watcher would catch the dot coming on), but closing
    // the terminal touches nothing, and a dot left lit sends a double-click looking for a
    // window that is gone. Background priority so it never competes with scrolling.
    private void StartLiveTimer()
    {
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _liveTimer.Tick += async (_, _) => await _vm.RefreshLiveAsync();
        _liveTimer.Start();
    }

    // A: hide/show completed · D: done · X: abandon · R: refresh · Ctrl+R: restart · F: fork.
    // Ignore while typing.
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        // Ctrl+R before plain R, or the refresh would swallow it.
        if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _ = _vm.RestartSelectedAsync(Grid.SelectedItems.OfType<SessionRow>().ToList());
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.A:
                _vm.ToggleHideCompleted();
                e.Handled = true;
                break;
            case Key.D:
                _vm.Mark(Grid.SelectedItems.OfType<SessionRow>().ToList(), Disposition.Done);
                e.Handled = true;
                break;
            case Key.X:
                _vm.Mark(Grid.SelectedItems.OfType<SessionRow>().ToList(), Disposition.Abandoned);
                e.Handled = true;
                break;
            case Key.R:
                if (_vm.RefreshCommand.CanExecute(null)) _vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F:
                ForkSelected();
                e.Handled = true;
                break;
        }
    }

    // F: fork the selected session — at the tip via the official --fork-session, or
    // from just before an earlier prompt via a truncated-copy fork (SessionForker).
    // Either way the original session file is never modified.
    private async void ForkSelected()
    {
        if (Grid.SelectedItems.OfType<SessionRow>().ToList() is not [var row])
        {
            _vm.StatusLine = "Select exactly one session to fork (F).";
            return;
        }

        var info = row.Info;
        if (string.IsNullOrEmpty(info.FilePath) || string.IsNullOrEmpty(info.Command))
        {
            _vm.StatusLine = "This session has no resumable file.";
            return;
        }

        IReadOnlyList<ForkPoint> points;
        try
        {
            points = await Task.Run(() => SessionForker.ListForkPoints(info.FilePath));
        }
        catch (Exception ex)
        {
            _vm.StatusLine = $"Could not read the session file: {ex.Message}";
            return;
        }

        var choices = new List<ForkChoice>
        {
            new("At the tip — everything up to now",
                "Official fork (claude --resume --fork-session)", null),
        };
        // Newest first, so recent fork points sit next to the tip option.
        foreach (var p in points.Reverse())
        {
            var when = p.Timestamp is DateTime t ? $"  ·  {t:yyyy-MM-dd HH:mm}" : "";
            choices.Add(new($"Before prompt #{p.Ordinal}{when}", $"“{p.Prompt}”", p.LeafUuid));
        }

        var dlg = new ForkDialog(row.Name, choices) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Choice is not { } choice) return;

        if (choice.LeafUuid is null)
        {
            Start(info.Command + " --fork-session");
            _vm.StatusLine = $"Forking \"{row.Name}\" at the tip in a new terminal.";
            return;
        }

        try
        {
            var newId = await Task.Run(() => SessionForker.ForkFrom(info.FilePath, choice.LeafUuid));
            Start($"cd \"{info.Cwd}\"; claude --resume {newId}");
            _vm.StatusLine = $"Forked \"{row.Name}\" ({choice.Title.ToLowerInvariant()}) → new session {newId[..8]}…";
        }
        catch (Exception ex)
        {
            _vm.StatusLine = $"Fork failed: {ex.Message}";
        }
    }

    // Double-click a row -> jump to the terminal already running this session if one is
    // open; otherwise open a new terminal in that folder and resume the session.
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not SessionRow row || string.IsNullOrEmpty(row.Command)) return;

        if (TryFocusRunning(row))
        {
            _vm.StatusLine = $"Switched to the terminal already running \"{row.Name}\".";
            return;
        }

        Start(_vm.ResumeCommandFor(row));
    }

    // True if this session is live in a terminal and we brought that window to the front.
    // Any failure (not running, or no focusable window) falls through to a fresh resume.
    private static bool TryFocusRunning(SessionRow row)
    {
        try
        {
            if (!LiveSessions.Scan().TryGetValue(row.Info.SessionId, out var running)) return false;
            return running.Any(session => SessionWindows.TryFocus(session.Pid));
        }
        catch
        {
            return false;
        }
    }

    private void RestartStaleBtn_Click(object sender, RoutedEventArgs e) =>
        _ = _vm.RestartStaleAsync();

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        var commands = Grid.SelectedItems.OfType<SessionRow>()
            .Select(_vm.ResumeCommandFor)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (commands.Count == 0)
        {
            _vm.StatusLine = "No rows selected.";
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, commands));
        _vm.StatusLine = $"Copied {commands.Count} resume command(s) to the clipboard.";
    }

    private void Changelog_Click(object sender, RoutedEventArgs e)
    {
        // ShellExecute so the URL opens in the default browser.
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/skfd/sky-session-claude/releases",
            UseShellExecute = true,
        });
    }

    // Where every terminal this window opens comes from; TerminalLauncher explains why it
    // takes the route it does.
    private static void Start(string command) => TerminalLauncher.Start(command);
}
