using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionCore;

namespace SessionApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private ProjectsWatcher? _watcher;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            await _vm.RefreshAsync();
            StartWatcher();
        };
        Closed += (_, _) => _watcher?.Dispose();
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

    // A: hide/show completed · X: abandon/restore · R: refresh. Ignore while typing.
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.A:
                _vm.ToggleHideCompleted();
                e.Handled = true;
                break;
            case Key.X:
                _vm.ToggleAbandoned(Grid.SelectedItems.OfType<SessionRow>().ToList());
                e.Handled = true;
                break;
            case Key.R:
                if (_vm.RefreshCommand.CanExecute(null)) _vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // Double-click a row -> open a new terminal in that folder and resume the session.
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not SessionRow row || string.IsNullOrEmpty(row.Command)) return;
        Start(row.Command);
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        var commands = Grid.SelectedItems.OfType<SessionRow>()
            .Select(r => r.Command)
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

    // If this app was itself launched from a Claude session, it inherited that session's
    // markers. Passing them on makes the resumed session think it is a nested child and
    // skip saving its transcript, so drop them. UseShellExecute must be false to edit the
    // child environment at all.
    private static void Start(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoExit", "-Command", command },
            UseShellExecute = false,
        };
        psi.Environment.Remove("CLAUDE_CODE_CHILD_SESSION");
        psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        Process.Start(psi);
    }
}
