using System.ComponentModel;
using SessionCore;

namespace SessionApp;

/// <summary>
/// Thin display wrapper over a <see cref="SessionInfo"/>. Keeps view-specific
/// formatting (two-line timestamp, "Ctx%" string) out of the core model. The
/// <see cref="Info"/> is swappable so a live refresh can update a row in place
/// (preserving the grid's selection and scroll) instead of rebuilding the list.
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

    // Two lines: absolute timestamp over a relative "how long ago".
    public string LastActiveDisplay =>
        $"{_info.LastActive:yyyy-MM-dd HH:mm}\n{TextUtil.RelativeAge(_info.LastActive)}";

    public DateTime LastActive => _info.LastActive;
    public string Name => _info.Name ?? "(untitled)";
    public string Project => _info.Project;
    public string Status => _info.Status.ToWire();
    public bool Complete => _info.Complete;
    public string CtxDisplay => _info.ContextPct is int p ? $"{p}%" : "";
    public string LastPrompt => _info.LastPrompt;
    public string Recap => _info.Recap;
    public double SizeKB => _info.SizeKB;
    public string Command => _info.Command;
}
