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
    /// Operator disposition: "unfinished, and I'm not going back to it". Never set by
    /// the scanner and never folded into <see cref="Complete"/> — see docs/GLOSSARY.md.
    /// </summary>
    public bool Abandoned
    {
        get => _abandoned;
        set
        {
            if (_abandoned == value) return;
            _abandoned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Abandoned)));
        }
    }

    private bool _abandoned;

    public DateTime LastActive => _info.LastActive;
    public string Timestamp => _info.LastActive.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// Age as shown on the card: "2 days ago", or "2 days ago -> 1h ago" when the
    /// session was opened again after that without a turn coming out of it.
    /// </summary>
    public string AgeDisplay => TextUtil.AgeDisplay(_info.LastActive, _info.LastTouched);

    /// <summary>Spells out both ends of <see cref="AgeDisplay"/> on hover.</summary>
    public string AgeTooltip => _info.LastTouched - _info.LastActive >= TextUtil.VisitGap
        ? $"Last turn: {Timestamp}" + Environment.NewLine
            + $"Last opened: {_info.LastTouched:yyyy-MM-dd HH:mm} (nothing said since)"
        : Timestamp;

    public string Name => _info.Name ?? "(untitled)";
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
