using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SessionCore;

namespace SessionApp;

public partial class MainViewModel : ObservableObject
{
    private readonly SessionScanner _scanner = new();

    /// <summary>Backing list; the grid binds to <see cref="RowsView"/> so filters apply.</summary>
    public ObservableCollection<SessionRow> Rows { get; } = new();

    public ICollectionView RowsView { get; }

    // --- filter-bar state ---------------------------------------------------
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hideCompleted = true;
    [ObservableProperty] private string _statusFilter = AllStatusesLabel;
    [ObservableProperty] private string _projectFilter = AllProjectsLabel;

    // --- scan options -------------------------------------------------------
    [ObservableProperty] private bool _allProjects = true;

    /// <summary>Effective row cap passed to the scanner ("All" maps to int.MaxValue).</summary>
    [ObservableProperty] private int _top = 50;

    /// <summary>Label bound to the "Show" dropdown; drives <see cref="Top"/>.</summary>
    [ObservableProperty] private string _topSelection = "50";

    public ObservableCollection<string> TopOptions { get; } =
        new() { "50", "100", "250", "500", "All" };

    /// <summary>When on, a filesystem watcher auto-refreshes on session file changes.</summary>
    [ObservableProperty] private bool _liveUpdates = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";

    public const string AllStatusesLabel = "(all statuses)";
    public const string AllProjectsLabel = "(all projects)";

    public ObservableCollection<string> StatusOptions { get; } = new() { AllStatusesLabel };
    public ObservableCollection<string> ProjectOptions { get; } = new() { AllProjectsLabel };

    public MainViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
    }

    // Re-apply filters whenever any filter input changes.
    partial void OnSearchTextChanged(string value) => RowsView.Refresh();
    partial void OnHideCompletedChanged(bool value) => RowsView.Refresh();
    partial void OnStatusFilterChanged(string value) => RowsView.Refresh();
    partial void OnProjectFilterChanged(string value) => RowsView.Refresh();

    // Changing scan scope re-scans (fire-and-forget; the command guards reentrancy).
    partial void OnAllProjectsChanged(bool value)
    {
        if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
    }

    partial void OnTopSelectionChanged(string value)
    {
        Top = value == "All" ? int.MaxValue : int.Parse(value);
        if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
    }

    private bool FilterRow(object obj)
    {
        if (obj is not SessionRow r) return false;

        if (HideCompleted && r.Complete) return false;
        if (StatusFilter != AllStatusesLabel && r.Status != StatusFilter) return false;
        if (ProjectFilter != AllProjectsLabel && r.Project != ProjectFilter) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            bool hit =
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.LastPrompt.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Recap.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }

    // Concurrent execution allowed so a live-watcher tick can refresh even while a
    // manual refresh is resolving; both resume on the UI thread and merge serially.
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusLine = "Scanning...";
        try
        {
            var options = new ScanOptions { All = AllProjects, Top = Top };
            // Parse off the UI thread; the cache makes repeat scans cheap.
            var infos = await Task.Run(() => _scanner.Scan(options));

            Merge(infos);
            RebuildFilterOptions();
            RowsView.Refresh();
            StatusLine = $"{infos.Count} session(s)  ·  {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reconcile the incoming scan into <see cref="Rows"/> in place, keyed by SessionId,
    /// so unchanged rows keep their identity (and the grid keeps selection + scroll).
    /// </summary>
    private void Merge(IReadOnlyList<SessionInfo> incoming)
    {
        var incomingIds = new HashSet<string>(incoming.Select(i => i.SessionId));
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!incomingIds.Contains(Rows[i].Info.SessionId)) Rows.RemoveAt(i);

        var bySid = Rows.ToDictionary(r => r.Info.SessionId);
        for (int idx = 0; idx < incoming.Count; idx++)
        {
            var info = incoming[idx];
            if (bySid.TryGetValue(info.SessionId, out var existing))
            {
                existing.Info = info;                       // in-place update; bindings refresh
                int cur = Rows.IndexOf(existing);
                if (cur != idx) Rows.Move(cur, idx);
            }
            else
            {
                var row = new SessionRow(info);
                Rows.Insert(idx, row);
                bySid[info.SessionId] = row;
            }
        }
    }

    /// <summary>Toggle the hide-completed filter (bound to the A hotkey).</summary>
    public void ToggleHideCompleted() => HideCompleted = !HideCompleted;

    private void RebuildFilterOptions()
    {
        var statuses = Rows.Select(r => r.Status).Distinct().OrderBy(s => s).ToList();
        StatusOptions.Clear();
        StatusOptions.Add(AllStatusesLabel);
        foreach (var s in statuses) StatusOptions.Add(s);
        if (!StatusOptions.Contains(StatusFilter)) StatusFilter = AllStatusesLabel;

        var projects = Rows.Select(r => r.Project).Distinct().OrderBy(p => p).ToList();
        ProjectOptions.Clear();
        ProjectOptions.Add(AllProjectsLabel);
        foreach (var p in projects) ProjectOptions.Add(p);
        if (!ProjectOptions.Contains(ProjectFilter)) ProjectFilter = AllProjectsLabel;
    }
}
