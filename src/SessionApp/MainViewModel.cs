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
    private readonly DispositionStore _dispositions = new();

    /// <summary>Backing list; the grid binds to <see cref="RowsView"/> so filters apply.</summary>
    public ObservableCollection<SessionRow> Rows { get; } = new();

    public ICollectionView RowsView { get; }

    // --- filter-bar state ---------------------------------------------------
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _hideCompleted = true;
    [ObservableProperty] private bool _showAbandoned;
    [ObservableProperty] private string _statusFilter = AllStatusesLabel;
    [ObservableProperty] private string _projectFilter = AllProjectsLabel;

    // --- scan options -------------------------------------------------------
    [ObservableProperty] private bool _allProjects = true;

    /// <summary>Effective row cap passed to the scanner ("All" maps to int.MaxValue).</summary>
    [ObservableProperty] private int _top = int.MaxValue;

    /// <summary>
    /// Label bound to the "Show" dropdown; drives <see cref="Top"/>. Defaults to "All":
    /// a capped list can hide an old unfinished session just past the cut, and the parse
    /// cache keys on (path, mtime, size), so the full scan is paid once per run.
    /// </summary>
    [ObservableProperty] private string _topSelection = "All";

    public ObservableCollection<string> TopOptions { get; } =
        new() { "50", "100", "250", "500", "All" };

    /// <summary>When on, a filesystem watcher auto-refreshes on session file changes.</summary>
    [ObservableProperty] private bool _liveUpdates = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";

    /// <summary>
    /// Footer build label, e.g. "v1.6.0 · 5c5afdc", from the assembly so it can never
    /// drift from the csproj version. The "· sha" comes from InformationalVersion's
    /// "+commit" suffix and is what tells two builds of the same version apart — so a
    /// rebuild is visibly current — and is dropped when the suffix is absent.
    /// </summary>
    public static string VersionLabel { get; } = BuildVersionLabel();

    private static string BuildVersionLabel()
    {
        var info = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..]
                ? a.InformationalVersion
                : "?";

        var parts = info.Split('+');
        var version = parts[0];
        var sha = parts.Length > 1 ? parts[1] : "";
        return sha.Length >= 7 ? $"v{version} · {sha[..7]}" : $"v{version}";
    }

    /// <summary>
    /// Window/taskbar caption: "Sky N sessions", N = rows still open (not completed,
    /// not abandoned) regardless of the current filters.
    /// </summary>
    [ObservableProperty] private string _windowTitle = "Sky sessions";

    public const string AllStatusesLabel = "(all statuses)";
    public const string AllProjectsLabel = "(all projects)";

    public ObservableCollection<string> StatusOptions { get; } = new() { AllStatusesLabel };
    public ObservableCollection<string> ProjectOptions { get; } = new() { AllProjectsLabel };

    /// <summary>
    /// Set by the view: runs the given update and re-applies the list selection afterwards.
    /// A view refresh (or a row move) raises a collection reset, and WPF's Selector drops
    /// the selection on reset, so the view has to put it back.
    /// </summary>
    public Action<Action>? SelectionKeeper { get; set; }

    public MainViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
    }

    private void KeepingSelection(Action update)
    {
        if (SelectionKeeper is null) update();
        else SelectionKeeper(update);
    }

    private void RefreshView() => KeepingSelection(RowsView.Refresh);

    // Re-apply filters whenever any filter input changes.
    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnHideCompletedChanged(bool value) => RefreshView();
    partial void OnShowAbandonedChanged(bool value) => RefreshView();
    partial void OnStatusFilterChanged(string value) => RefreshView();
    partial void OnProjectFilterChanged(string value) => RefreshView();

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

        if (HideCompleted && r.Settled) return false;
        if (!ShowAbandoned && r.Abandoned) return false;
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

            KeepingSelection(() =>
            {
                Merge(infos);
                RebuildFilterOptions();
                RowsView.Refresh();
            });
            await RefreshLiveAsync();
            UpdateWindowTitle();
            StatusLine = $"{infos.Count} session(s)  ·  {DateTime.Now:HH:mm:ss}"
                + "  —  double-click to resume · A: hide/show completed · D: done · X: abandon"
                + " · R: refresh · Ctrl+R: restart · F: fork";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Light the dot on every row whose session is open in a terminal right now.
    /// Called after each scan and on the view's timer — a terminal closing writes to no
    /// session file, so the filesystem watcher would never notice the dot going out.
    /// Cheap enough to poll: a handful of small registry files plus a pid lookup each.
    /// </summary>
    public async Task RefreshLiveAsync()
    {
        Dictionary<string, List<LiveSession>> live;
        try
        {
            live = await Task.Run(LiveSessions.Scan);
        }
        catch
        {
            return;   // registry unreadable — leave the dots as they were
        }

        foreach (var row in Rows)
            row.Live = live.TryGetValue(row.Info.SessionId, out var running) && running.Count > 0
                ? running[0]
                : null;

        UpdateStaleCount();
    }

    // --- restarting ---------------------------------------------------------

    /// <summary>
    /// How many running sessions are behind the installed build and can be swept up without
    /// asking. Drives the toolbar button's label, so it says what the button will actually do.
    /// </summary>
    [ObservableProperty] private int _staleCount;

    [ObservableProperty] private bool _isRestarting;

    /// <summary>A restart drives someone's terminal; two at once would fight over it.</summary>
    public bool NotRestarting => !IsRestarting;

    public string RestartStaleLabel =>
        StaleCount > 0 ? $"Restart stale ({StaleCount})" : "Restart stale";

    partial void OnStaleCountChanged(int value) =>
        OnPropertyChanged(nameof(RestartStaleLabel));

    partial void OnIsRestartingChanged(bool value) =>
        OnPropertyChanged(nameof(NotRestarting));

    private void UpdateStaleCount() => StaleCount = Sweepable().Count;

    /// <summary>
    /// The sessions a sweep may take: running, behind the installed build, provably idle,
    /// and not ones you have crossed out. Anything merely plausible is left for you to
    /// restart yourself — see <see cref="RestartPolicy"/>.
    /// </summary>
    private List<SessionRow> Sweepable() =>
        Rows.Where(r => r.IsLive && r.IsStale && !r.Abandoned && r.Verdict.CanSweep).ToList();

    /// <summary>Restart the rows the operator picked, including the ones only offered.</summary>
    public async Task RestartSelectedAsync(IReadOnlyList<SessionRow> rows)
    {
        var targets = rows.Where(r => r.CanRestart).ToList();
        if (targets.Count == 0)
        {
            var blocked = rows.FirstOrDefault(r => r.IsLive);
            StatusLine = blocked is not null
                ? $"Cannot restart \"{blocked.Name}\": {blocked.Verdict.Reason}."
                : "Select a session that is open in a terminal.";
            return;
        }

        await RestartAll(targets, Array.Empty<SessionRow>());
    }

    /// <summary>
    /// Restart every stale session that is provably idle, and account for the ones skipped —
    /// silence about them would read as "all done" when half the terminals still nag.
    /// </summary>
    public async Task RestartStaleAsync()
    {
        var targets = Sweepable();
        var skipped = Rows.Where(r => r.IsLive && r.IsStale && !r.Abandoned && !r.Verdict.CanSweep).ToList();

        if (targets.Count == 0)
        {
            StatusLine = skipped.Count == 0
                ? "Nothing to restart — every running session is on the installed build."
                : $"Nothing can be restarted unattended right now: {Tally(skipped)}.";
            return;
        }

        await RestartAll(targets, skipped);
    }

    private async Task RestartAll(IReadOnlyList<SessionRow> targets, IReadOnlyList<SessionRow> skipped)
    {
        if (IsRestarting) return;
        IsRestarting = true;
        try
        {
            int done = 0;
            var failures = new List<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var row = targets[i];
                if (row.Live is not { } live) continue;

                StatusLine = $"Restarting \"{row.Name}\" ({i + 1} of {targets.Count})…";

                var result = await SessionRestarter.RestartAsync(live);
                if (result.Ok) done++;
                else failures.Add($"\"{row.Name}\" — {result.Message}");

                await RefreshLiveAsync();
            }

            var parts = new List<string> { $"Restarted {done} of {targets.Count}" };
            if (skipped.Count > 0) parts.Add($"skipped {skipped.Count} ({Tally(skipped)})");
            if (failures.Count > 0) parts.Add(string.Join("; ", failures));
            StatusLine = string.Join("  ·  ", parts) + ".";
        }
        finally
        {
            IsRestarting = false;
        }
    }

    /// <summary>"2 busy, 4 run under the desktop app" — the reasons, counted.</summary>
    private static string Tally(IEnumerable<SessionRow> rows) =>
        string.Join(", ", rows
            .GroupBy(r => r.Verdict.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}"));

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
                var row = new SessionRow(info)
                {
                    Disposition = _dispositions.Get(info.SessionId),
                };
                Rows.Insert(idx, row);
                bySid[info.SessionId] = row;
            }
        }
    }

    /// <summary>Toggle the hide-completed filter (bound to the A hotkey).</summary>
    public void ToggleHideCompleted() => HideCompleted = !HideCompleted;

    /// <summary>
    /// Record the operator's verdict on the given rows: Done (D) or Abandoned (X).
    /// Pressing the same key again clears the mark, so one key both sets and undoes it,
    /// and a mixed selection sets — one keypress covers everything selected.
    /// </summary>
    public void Mark(IReadOnlyList<SessionRow> rows, Disposition disposition)
    {
        if (rows.Count == 0)
        {
            StatusLine = "No rows selected.";
            return;
        }

        var target = rows.Any(r => r.Disposition != disposition) ? disposition : Disposition.None;
        foreach (var r in rows)
        {
            r.Disposition = target;
            _dispositions.Set(r.Info.SessionId, target);
        }

        RefreshView();
        UpdateWindowTitle();
        // Marked rows drop out of the list under the default filters, so say where they went.
        StatusLine = target switch
        {
            Disposition.Done => $"Marked {rows.Count} session(s) done."
                + (HideCompleted ? "  Untick \"Hide completed\" to see them." : ""),
            Disposition.Abandoned => $"Abandoned {rows.Count} session(s)."
                + (ShowAbandoned ? "" : "  Tick \"Show abandoned\" to see them."),
            _ => $"Restored {rows.Count} session(s).",
        };
    }

    private void UpdateWindowTitle()
    {
        int n = Rows.Count(r => !r.Settled && !r.Abandoned);
        WindowTitle = $"Sky {n} session{(n == 1 ? "" : "s")}";
    }

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
