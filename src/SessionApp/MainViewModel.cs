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
    [ObservableProperty] private bool _hideCompleted;
    [ObservableProperty] private string _statusFilter = AllStatusesLabel;
    [ObservableProperty] private string _projectFilter = AllProjectsLabel;

    // --- scan options -------------------------------------------------------
    [ObservableProperty] private bool _allProjects = true;
    [ObservableProperty] private int _top = 50;

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

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusLine = "Scanning...";
        try
        {
            var options = new ScanOptions { All = AllProjects, Top = Top };
            // Parse off the UI thread; M3 will turn this into a streaming/incremental scan.
            var infos = await Task.Run(() => _scanner.Scan(options));

            Rows.Clear();
            foreach (var info in infos) Rows.Add(new SessionRow(info));

            RebuildFilterOptions();
            RowsView.Refresh();
            StatusLine = $"{infos.Count} session(s)  ·  {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
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
