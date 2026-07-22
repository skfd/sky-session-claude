using System.ComponentModel;
using System.Windows.Media;
using SessionCore;

namespace SessionApp;

/// <summary>
/// Thin display wrapper over a <see cref="SessionInfo"/>. Keeps view-specific
/// formatting (relative age, "Ctx%" string, status colour) out of the core model.
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
    public string RelativeAge => TextUtil.RelativeAge(_info.LastActive);
    public string Timestamp => _info.LastActive.ToString("yyyy-MM-dd HH:mm");
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

    /// <summary>Accent colour for the status dot and the card's left edge.</summary>
    public Brush StatusBrush => _info.Status switch
    {
        SessionStatus.Complete => Brushes.Transparent,
        SessionStatus.WaitingYou => Amber,
        SessionStatus.WaitingAgent => Blue,
        SessionStatus.CutOff => Red,
        SessionStatus.Limit => Purple,
        SessionStatus.Error => Red,
        SessionStatus.Interrupted => Orange,
        _ => Brushes.Transparent,
    };

    // Darkened from the Tailwind-500 shades so each stripe clears 3:1 against
    // the white card background (WCAG 1.4.11, non-text contrast).
    private static readonly Brush Amber = Frozen("#A16207");
    private static readonly Brush Blue = Frozen("#1D4ED8");
    private static readonly Brush Red = Frozen("#B91C1C");
    private static readonly Brush Purple = Frozen("#7E22CE");
    private static readonly Brush Orange = Frozen("#C2410C");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
