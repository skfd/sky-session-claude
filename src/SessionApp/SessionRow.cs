using System.ComponentModel;
using SessionCore;

namespace SessionApp;

/// <summary>
/// Thin display wrapper over a <see cref="SessionInfo"/>. Keeps view-specific
/// formatting (relative age, "Ctx%" string, meta line) out of the core model.
/// The <see cref="Info"/> is swappable so a live refresh can update a row in place
/// (preserving the list's selection and scroll) instead of rebuilding the list.
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    private SessionInfo _info;

    public SessionRow(SessionInfo info) => _info = info;

    public SessionInfo Info
    {
        get => _info;
        set
        {
            _info = value;
            // Null name signals "all properties changed" so every binding re-reads.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The operator's own verdict on this session — never the scanner's, and never
    /// folded into <see cref="Status"/>: an abandoned cut-off session stays cut-off,
    /// and a session marked Done keeps whatever status it earned. See docs/GLOSSARY.md.
    /// </summary>
    public Disposition Disposition
    {
        get => _disposition;
        set
        {
            if (_disposition == value) return;
            _disposition = value;
            // Everything derived from it has to re-read, filters and title included.
            foreach (var name in new[]
                     { nameof(Disposition), nameof(Abandoned), nameof(Done), nameof(Settled) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private Disposition _disposition;

    /// <summary>"Unfinished, and I'm not going back to it" (X). Crossed out on the card.</summary>
    public bool Abandoned => _disposition == Disposition.Abandoned;

    /// <summary>"Finished, whatever the classifier says" (D). Ticked on the card.</summary>
    public bool Done => _disposition == Disposition.Done;

    /// <summary>
    /// Nothing left to do here: the classifier says complete, or you said so with D.
    /// This — not <see cref="Complete"/> — is what "Hide completed" hides.
    /// </summary>
    public bool Settled => Complete || Done;

    /// <summary>
    /// The interactive CLI running this session in a terminal right now, or null when none
    /// is (see <see cref="LiveSessions"/>). Everything the card can say about a running
    /// session — the dot, the build it is on, whether Remote Control is connected, whether
    /// it is safe to restart — comes from here.
    ///
    /// Refreshed on a timer, not by the file watcher: closing a terminal writes nothing.
    /// </summary>
    public LiveSession? Live
    {
        get => _live;
        set
        {
            if (_live?.Pid == value?.Pid
                && _live?.Version == value?.Version
                && _live?.Status == value?.Status
                && _live?.RemoteControl == value?.RemoteControl) return;

            _live = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private LiveSession? _live;

    /// <summary>
    /// Open in a terminal right now. Lights the dot on the card, and is the same condition
    /// under which a double-click jumps to that window instead of resuming.
    /// </summary>
    public bool IsLive => _live is not null;

    /// <summary>Remote Control is connected, so this session is reachable from your phone.</summary>
    public bool RemoteControl => _live?.RemoteControl == true;

    /// <summary>
    /// Running an older build than the one installed — this is the session that keeps asking
    /// to be restarted.
    /// </summary>
    public bool IsStale => ClaudeInstall.IsStale(_live?.Version, ClaudeInstall.InstalledVersion);

    /// <summary>The build it is stuck on, for the chip on the card.</summary>
    public string StaleDisplay => _live?.Version ?? "";

    public string StaleTooltip =>
        $"Running {_live?.Version}; {ClaudeInstall.InstalledVersion} is installed. Restart to pick it up.";

    /// <summary>
    /// Whether this session may be restarted for you, and why not when it may not.
    /// Recomputed on read because "only just went idle" is a fact about the clock.
    /// </summary>
    public SweepVerdict Verdict => _live is null
        ? new SweepVerdict(SweepSafety.Unsafe, "not running")
        : RestartPolicy.Judge(_live, _info.Status, DateTime.Now);

    /// <summary>Restart is offered for anything not flatly refused; the sweep takes only the safe ones.</summary>
    public bool CanRestart => IsLive && Verdict.Safety != SweepSafety.Unsafe;

    public string RestartTooltip => _live is null
        ? "Not running in a terminal"
        : Verdict.Safety switch
        {
            SweepSafety.Safe => $"Restart in place (Ctrl+R){RemoteControlNote}",
            SweepSafety.Ask => $"Restartable, but: {Verdict.Reason}{RemoteControlNote}",
            _ => $"Cannot restart: {Verdict.Reason}",
        };

    private string RemoteControlNote =>
        RemoteControl ? "\nRemote Control is on and will be reconnected." : "";

    public DateTime LastActive => _info.LastActive;
    public string Timestamp => _info.LastActive.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// Age as shown on the card: "1h ago", or "2 days ago -> 1h ago" when an earlier
    /// sitting preceded this one. Reopening the session moves neither date.
    /// </summary>
    public string AgeDisplay => TextUtil.AgeDisplay(_info.LastActive, _info.PreviousActive);

    /// <summary>
    /// Absolute times behind the card's age, plus the one fact the card deliberately
    /// leaves out: when the session was last opened, which says nothing was said since.
    /// </summary>
    public string AgeTooltip
    {
        get
        {
            var lines = new List<string>();
            if (_info.PreviousActive is { } prev) lines.Add($"Worked on: {prev:yyyy-MM-dd HH:mm}");
            lines.Add($"Last turn: {Timestamp}");
            if (_info.LastTouched - _info.LastActive >= TextUtil.SittingGap)
                lines.Add($"Last opened: {_info.LastTouched:yyyy-MM-dd HH:mm} (nothing said since)");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string Name => _info.Name ?? "(untitled)";

    /// <summary>The session's own title, or null when it has not earned one — what a
    /// relaunch names it after, where <see cref="Name"/> is only for display.</summary>
    public string? Title => _info.Title;
    public string Project => _info.Project;
    public string Status => _info.Status.ToWire();
    public bool Complete => _info.Complete;
    public string CtxDisplay => _info.ContextPct is int p
        ? (_info.IsLargeContext ? $"{p}% · 1M" : $"{p}%")
        : "";
    public string LastPrompt => _info.LastPrompt;
    public string Recap => _info.Recap;
    public double SizeKB => _info.SizeKB;
    public string Command => _info.Command;

    /// <summary>Third line of the card: status · context · file size.</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string> { Status };
            if (CtxDisplay.Length > 0) parts.Add($"ctx {CtxDisplay}");
            parts.Add(SizeKB >= 1024 ? $"{SizeKB / 1024:0.#} MB" : $"{SizeKB:0} KB");
            return string.Join("  ·  ", parts);
        }
    }
}
