using SessionCore;

namespace SessionCore.Tests;

/// <summary>
/// Locks "last active" to the last real turn in the session file. Resuming a session
/// appends untimestamped metadata records, which bumps the file's last-write time — the
/// age shown must survive that, because how long a session has been sitting is exactly
/// what you look at before deciding whether to go back to it.
/// </summary>
public class LastActiveTests
{
    private const string TurnAt = "2026-08-01T10:00:00.000Z";
    private const string EarlierTurnAt = "2026-07-30T09:00:00.000Z";

    private static string Asst(string ts, string text) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"message\":{\"model\":\"claude-sonnet-4-6\""
        + ",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string User(string ts, string text) =>
        "{\"type\":\"user\",\"timestamp\":\"" + ts + "\",\"message\":{\"content\":\"" + text + "\"}}";

    /// <summary>The records `claude --resume` appends before you have typed anything.</summary>
    private static readonly string[] ResumeNoise =
    [
        "{\"type\":\"last-prompt\",\"lastPrompt\":\"do the thing\"}",
        "{\"type\":\"mode\",\"mode\":\"default\"}",
        "{\"type\":\"permission-mode\",\"permissionMode\":\"default\"}",
        "{\"type\":\"atis-latch\",\"atis\":true}",
    ];

    // --- parser --------------------------------------------------------------

    [Fact]
    public void LastTurnUtc_IsTheLastRealTurn()
    {
        var f = SessionFileParser.Parse([User(EarlierTurnAt, "go"), Asst(TurnAt, "done")]);
        Assert.Equal(DateTime.Parse(TurnAt).ToUniversalTime(), f.LastTurnUtc);
    }

    [Fact]
    public void LastTurnUtc_IgnoresUntimestampedMetadata()
    {
        var f = SessionFileParser.Parse([Asst(TurnAt, "done"), .. ResumeNoise]);
        Assert.Equal(DateTime.Parse(TurnAt).ToUniversalTime(), f.LastTurnUtc);
    }

    [Fact]
    public void LastTurnUtc_IgnoresHarnessTurns()
    {
        var later = "2026-08-05T12:00:00.000Z";
        var f = SessionFileParser.Parse(
            [Asst(TurnAt, "done"), User(later, "<system-reminder>context</system-reminder>")]);
        Assert.Equal(DateTime.Parse(TurnAt).ToUniversalTime(), f.LastTurnUtc);
    }

    [Fact]
    public void LastTurnUtc_IsNull_WhenNoTurnCarriesATimestamp()
    {
        var f = SessionFileParser.Parse(ResumeNoise);
        Assert.Null(f.LastTurnUtc);
    }

    // --- scanner -------------------------------------------------------------

    private static (string dir, FileInfo file) WriteSession(IEnumerable<string> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sky-{Guid.NewGuid():N}", "C--Users-kk-Code-demo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid()}.jsonl");
        File.WriteAllLines(path, lines);
        return (Path.GetDirectoryName(dir)!, new FileInfo(path));
    }

    [Fact]
    public void ResumingASession_DoesNotResetItsAge()
    {
        var (root, file) = WriteSession([Asst(TurnAt, "done"), .. ResumeNoise]);
        try
        {
            // What a resume leaves behind: metadata appended now, over an old conversation.
            File.SetCreationTime(file.FullName, DateTime.Parse(TurnAt).ToLocalTime());
            File.SetLastWriteTime(file.FullName, DateTime.Now);

            var row = new SessionScanner(root).Scan(new ScanOptions()).Single();

            Assert.Equal(DateTime.Parse(TurnAt).ToUniversalTime(), row.LastActive.ToUniversalTime());
            Assert.True(row.AgeDays > 1);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FallsBackToLastWrite_WhenNoTurnCarriesATimestamp()
    {
        var (root, file) = WriteSession(ResumeNoise);
        try
        {
            var written = DateTime.Now.AddDays(-3);
            File.SetCreationTime(file.FullName, written);
            File.SetLastWriteTime(file.FullName, written);

            var row = new SessionScanner(root).Scan(new ScanOptions()).Single();

            Assert.Equal(written, row.LastActive, TimeSpan.FromSeconds(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFreshFileOfOldRecords_ReadsAsNew()
    {
        // A fork copies the original's records, timestamps and all, into a new file.
        var (root, _) = WriteSession([User(EarlierTurnAt, "go"), Asst(TurnAt, "done")]);
        try
        {
            var row = new SessionScanner(root).Scan(new ScanOptions()).Single();
            Assert.Equal(DateTime.Now, row.LastActive, TimeSpan.FromMinutes(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LastTouched_IsTheFilesLastWrite_EvenWhenNoTurnFollowed()
    {
        var (root, file) = WriteSession([Asst(TurnAt, "done"), .. ResumeNoise]);
        try
        {
            var opened = DateTime.Now.AddHours(-1);
            File.SetCreationTime(file.FullName, DateTime.Parse(TurnAt).ToLocalTime());
            File.SetLastWriteTime(file.FullName, opened);

            var row = new SessionScanner(root).Scan(new ScanOptions()).Single();

            Assert.Equal(DateTime.Parse(TurnAt).ToUniversalTime(), row.LastActive.ToUniversalTime());
            Assert.Equal(opened, row.LastTouched, TimeSpan.FromSeconds(1));
        }
        finally { Directory.Delete(root, true); }
    }

    // --- the two-age card label ----------------------------------------------

    [Fact]
    public void AgeDisplay_ShowsBothEnds_WhenTheSessionWasOpenedWithoutATurn()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0);
        var display = TextUtil.AgeDisplay(now.AddDays(-2), now.AddHours(-1), now);
        Assert.Equal("2 days ago → 1h ago", display);
    }

    [Fact]
    public void AgeDisplay_StaysSingle_WhileTheSessionIsBeingWorkedOn()
    {
        // Every turn rewrites the file seconds later; that is not a visit.
        var now = new DateTime(2026, 8, 21, 12, 0, 0);
        var turn = now.AddMinutes(-30);
        Assert.Equal("30m ago", TextUtil.AgeDisplay(turn, turn.AddSeconds(2), now));
    }

    /// <summary>
    /// The whole rule in one pass: reopening a session leaves it exactly where it was
    /// in the list, and the first new turn — not the reopen — is what refloats it.
    /// </summary>
    [Fact]
    public void ReopeningHoldsPosition_TheFirstNewTurnRefloats()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sky-{Guid.NewGuid():N}", "C--Users-kk-Code-demo");
        Directory.CreateDirectory(dir);
        var root = Path.GetDirectoryName(dir)!;
        try
        {
            var older = Path.Combine(dir, $"{Guid.NewGuid()}.jsonl");
            File.WriteAllLines(older, [Asst(EarlierTurnAt, "older session")]);
            File.SetCreationTime(older, DateTime.Parse(EarlierTurnAt).ToLocalTime());

            var newer = Path.Combine(dir, $"{Guid.NewGuid()}.jsonl");
            File.WriteAllLines(newer, [Asst(TurnAt, "newer session")]);
            File.SetCreationTime(newer, DateTime.Parse(EarlierTurnAt).ToLocalTime());

            var scanner = new SessionScanner(root);
            Assert.Equal(Path.GetFileNameWithoutExtension(newer), scanner.Scan(new ScanOptions())[0].SessionId);

            // Reopen the older one: bookkeeping records appended, nothing said.
            File.AppendAllLines(older, ResumeNoise);
            File.SetLastWriteTime(older, DateTime.Now);
            Assert.Equal(Path.GetFileNameWithoutExtension(newer), scanner.Scan(new ScanOptions())[0].SessionId);

            // Now actually say something.
            File.AppendAllLines(older, [User("2026-08-10T08:00:00.000Z", "carry on")]);
            Assert.Equal(Path.GetFileNameWithoutExtension(older), scanner.Scan(new ScanOptions())[0].SessionId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_SortsByLastActive_NotByLastWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sky-{Guid.NewGuid():N}", "C--Users-kk-Code-demo");
        Directory.CreateDirectory(dir);
        var root = Path.GetDirectoryName(dir)!;
        try
        {
            var resumed = Path.Combine(dir, $"{Guid.NewGuid()}.jsonl");
            File.WriteAllLines(resumed, [Asst(EarlierTurnAt, "old"), .. ResumeNoise]);
            File.SetCreationTime(resumed, DateTime.Parse(EarlierTurnAt).ToLocalTime());
            File.SetLastWriteTime(resumed, DateTime.Now);            // just resumed, no work done

            var worked = Path.Combine(dir, $"{Guid.NewGuid()}.jsonl");
            File.WriteAllLines(worked, [Asst(TurnAt, "newer")]);
            File.SetCreationTime(worked, DateTime.Parse(EarlierTurnAt).ToLocalTime());
            File.SetLastWriteTime(worked, DateTime.Now.AddMinutes(-5));

            var rows = new SessionScanner(root).Scan(new ScanOptions());

            Assert.Equal(Path.GetFileNameWithoutExtension(worked), rows[0].SessionId);
        }
        finally { Directory.Delete(root, true); }
    }
}
