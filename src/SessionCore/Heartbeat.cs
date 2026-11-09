using System.Text.Json;

namespace SessionCore;

/// <summary>
/// What a running Sky says about itself, every half minute, in <c>heartbeat.json</c> beside its
/// other files: that it is alive, that its tick is turning, when the keeper last looked, and
/// which write of each shared store it is holding.
///
/// Sky is the only process that keeps standby up and the only long-lived reader of the
/// sidecars, and both have failed silently: the keeper because nothing outside Sky could see
/// it had stopped (hosts dead for days, 10-08), the reader because it never re-read
/// (declarations frozen at launch, 10-12). <c>standby.log</c> only gets a line when the keeper
/// does something, so a quiet log could not tell "all up" from "Sky gone". This can, and
/// <see cref="Doctor"/> is what reads it.
/// </summary>
public sealed record Heartbeat
{
    public required DateTimeOffset At { get; init; }
    public required int Pid { get; init; }
    public string? Version { get; init; }

    /// <summary>Whether this Sky runs the keeper at all — off for a <c>--multi</c> instance.</summary>
    public bool KeeperOn { get; init; }

    /// <summary>Whether its keeper may grant folder trust (a <c>--standby</c> start).</summary>
    public bool KeeperMayGrantTrust { get; init; }

    /// <summary>When the keeper last took its turn; null when it has not yet.</summary>
    public DateTimeOffset? KeeperLookedAt { get; init; }

    /// <summary>Each shared store's file name, with the write time (UTC) Sky holds of it.</summary>
    public Dictionary<string, DateTime> Stores { get; init; } = [];

    public const string FileName = "heartbeat.json";

    public static string DefaultPath => Path.Combine(DispositionStore.DefaultDir(), FileName);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Write beside and move over, so a reader never sees half a file. Never throws.</summary>
    public void Write(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A missed beat is what the doctor is there to notice; it is not Sky's failure.
        }
    }

    /// <summary>The last beat written, or null when there is none or it cannot be read.</summary>
    public static Heartbeat? Read(string? path = null)
    {
        try { return JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(path ?? DefaultPath)); }
        catch { return null; }
    }
}
