using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LetterboxdSync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for the static SyncHistory entry points. The existing SyncHistoryTests
/// suite verifies the JSONL serialisation contract directly; these tests drive
/// the public API (Record / GetRecent / GetPage / GetLastStatusForFilm / GetStats /
/// WasSuccessfullySynced / GetLastSuccessfulSyncDate) end-to-end via the new
/// DataPathOverride hook so file I/O is isolated to a temp file per test.
/// </summary>
[Collection("SyncHistory")]
public class SyncHistoryStaticTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataPath;

    public SyncHistoryStaticTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dataPath = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.DataPathOverride = _dataPath;
        SyncHistory.ResetForTesting();
        SyncHistory.SetLogger(NullLogger.Instance);
    }

    public void Dispose()
    {
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static SyncEvent MakeEvent(string username, int tmdbId, SyncStatus status,
        DateTime? viewingDate = null, DateTime? timestamp = null, string title = "Test")
    {
        return new SyncEvent
        {
            FilmTitle = title,
            FilmSlug = title.ToLower().Replace(" ", "-"),
            TmdbId = tmdbId,
            Username = username,
            Timestamp = timestamp ?? DateTime.UtcNow,
            ViewingDate = viewingDate,
            Status = status,
            Source = "test"
        };
    }

    [Fact]
    public void Record_AppendsToFile()
    {
        SyncHistory.Record(MakeEvent("alice", 1, SyncStatus.Success));

        Assert.True(File.Exists(_dataPath));
        var lines = File.ReadAllLines(_dataPath);
        Assert.Single(lines);
        var evt = JsonSerializer.Deserialize<SyncEvent>(lines[0]);
        Assert.Equal("alice", evt!.Username);
    }

    [Fact]
    public void Record_MultipleEvents_AppendsAllLines()
    {
        SyncHistory.Record(MakeEvent("alice", 1, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("alice", 2, SyncStatus.Failed));
        SyncHistory.Record(MakeEvent("bob", 3, SyncStatus.Skipped));

        var lines = File.ReadAllLines(_dataPath);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        var oldEvent = MakeEvent("alice", 1, SyncStatus.Success, timestamp: DateTime.UtcNow.AddHours(-2));
        var newEvent = MakeEvent("alice", 2, SyncStatus.Success, timestamp: DateTime.UtcNow);
        SyncHistory.Record(oldEvent);
        SyncHistory.Record(newEvent);
        SyncHistory.ResetForTesting(); // force re-read from disk

        var recent = SyncHistory.GetRecent();

        Assert.Equal(2, recent.Count);
        Assert.Equal(2, recent[0].TmdbId); // newest first
        Assert.Equal(1, recent[1].TmdbId);
    }

    [Fact]
    public void GetRecent_FilteredByUsername_OnlyReturnsThatUser()
    {
        SyncHistory.Record(MakeEvent("alice", 1, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("bob", 2, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("alice", 3, SyncStatus.Success));
        SyncHistory.ResetForTesting();

        var aliceOnly = SyncHistory.GetRecent(username: "alice");

        Assert.Equal(2, aliceOnly.Count);
        Assert.All(aliceOnly, e => Assert.Equal("alice", e.Username));
    }

    [Fact]
    public void GetRecent_RespectsCountCap()
    {
        for (int i = 0; i < 10; i++)
            SyncHistory.Record(MakeEvent("alice", i, SyncStatus.Success));
        SyncHistory.ResetForTesting();

        var recent = SyncHistory.GetRecent(count: 3);

        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public void GetPage_OffsetAndCount_PaginateNewestFirst()
    {
        for (int i = 0; i < 10; i++)
            SyncHistory.Record(MakeEvent("alice", i, SyncStatus.Success,
                timestamp: DateTime.UtcNow.AddSeconds(-i)));
        SyncHistory.ResetForTesting();

        var (page, total) = SyncHistory.GetPage(offset: 2, count: 3);

        Assert.Equal(10, total);
        Assert.Equal(3, page.Count);
        // Offset 2 of newest-first means we skipped 0 and 1 (the freshest); next is 2.
        Assert.Equal(2, page[0].TmdbId);
    }

    [Fact]
    public void GetPage_FilteredByUsername_OnlyCountsThatUser()
    {
        for (int i = 0; i < 5; i++)
            SyncHistory.Record(MakeEvent("alice", i, SyncStatus.Success));
        for (int i = 0; i < 3; i++)
            SyncHistory.Record(MakeEvent("bob", 100 + i, SyncStatus.Success));
        SyncHistory.ResetForTesting();

        var (page, total) = SyncHistory.GetPage(0, 100, username: "bob");

        Assert.Equal(3, total);
        Assert.All(page, e => Assert.Equal("bob", e.Username));
    }

    [Fact]
    public void GetStats_AggregatesAllStatuses()
    {
        SyncHistory.Record(MakeEvent("alice", 1, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("alice", 2, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("alice", 3, SyncStatus.Failed));
        SyncHistory.Record(MakeEvent("alice", 4, SyncStatus.Skipped));
        SyncHistory.Record(MakeEvent("alice", 5, SyncStatus.Rewatch));
        SyncHistory.ResetForTesting();

        var (total, success, failed, skipped, rewatches) = SyncHistory.GetStats();

        Assert.Equal(5, total);
        Assert.Equal(2, success);
        Assert.Equal(1, failed);
        Assert.Equal(1, skipped);
        Assert.Equal(1, rewatches);
    }

    [Fact]
    public void GetStats_FilteredByUsername_OnlyCountsThatUser()
    {
        SyncHistory.Record(MakeEvent("alice", 1, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("bob", 2, SyncStatus.Success));
        SyncHistory.Record(MakeEvent("bob", 3, SyncStatus.Failed));
        SyncHistory.ResetForTesting();

        var (total, success, failed, _, _) = SyncHistory.GetStats(username: "bob");

        Assert.Equal(2, total);
        Assert.Equal(1, success);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void WasSuccessfullySynced_TrueForExactMatch()
    {
        var date = DateTime.Today;
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Success, viewingDate: date));
        SyncHistory.ResetForTesting();

        Assert.True(SyncHistory.WasSuccessfullySynced("alice", 100, date));
    }

    [Fact]
    public void WasSuccessfullySynced_FalseForDifferentDate()
    {
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Success, viewingDate: DateTime.Today));
        SyncHistory.ResetForTesting();

        Assert.False(SyncHistory.WasSuccessfullySynced("alice", 100, DateTime.Today.AddDays(-1)));
    }

    [Fact]
    public void GetLastStatusForFilm_ReturnsMostRecent()
    {
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Failed,
            timestamp: DateTime.UtcNow.AddHours(-1)));
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Success,
            timestamp: DateTime.UtcNow));
        SyncHistory.ResetForTesting();

        Assert.Equal(SyncStatus.Success, SyncHistory.GetLastStatusForFilm("alice", 100));
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_ReturnsViewingDateOfMostRecentSuccess()
    {
        var oldDate = DateTime.Today.AddDays(-3);
        var newerDate = DateTime.Today.AddDays(-1);
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Success,
            viewingDate: oldDate, timestamp: DateTime.UtcNow.AddHours(-5)));
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Success,
            viewingDate: newerDate, timestamp: DateTime.UtcNow));
        SyncHistory.ResetForTesting();

        var lastDate = SyncHistory.GetLastSuccessfulSyncDate("alice", 100);

        Assert.Equal(newerDate, lastDate);
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_NullWhenNoSuccess()
    {
        SyncHistory.Record(MakeEvent("alice", 100, SyncStatus.Failed,
            viewingDate: DateTime.Today));
        SyncHistory.ResetForTesting();

        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate("alice", 100));
    }

    [Fact]
    public void Load_FromExistingFile_ParsesAllLines()
    {
        // Pre-seed the file before any access; the cache should load everything.
        var events = new[]
        {
            MakeEvent("alice", 1, SyncStatus.Success),
            MakeEvent("alice", 2, SyncStatus.Failed)
        };
        File.WriteAllLines(_dataPath,
            events.Select(e => JsonSerializer.Serialize(e)));

        var recent = SyncHistory.GetRecent();

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void Load_FileWithMalformedLine_SkipsItContinuesParsing()
    {
        File.WriteAllLines(_dataPath, new[]
        {
            JsonSerializer.Serialize(MakeEvent("alice", 1, SyncStatus.Success)),
            "{ malformed json line",
            JsonSerializer.Serialize(MakeEvent("bob", 2, SyncStatus.Success))
        });

        var recent = SyncHistory.GetRecent();

        // Two valid events parsed, one malformed line silently dropped.
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void Load_LegacyJsonFormat_MigratesToJsonl()
    {
        // Older versions wrote the history as a single JSON array file. The loader
        // should migrate it to JSONL on first access.
        var legacyPath = _dataPath.Replace(".jsonl", ".json");
        var seed = new List<SyncEvent>
        {
            MakeEvent("alice", 1, SyncStatus.Success),
            MakeEvent("alice", 2, SyncStatus.Rewatch)
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(seed));

        var recent = SyncHistory.GetRecent();

        Assert.Equal(2, recent.Count);
        // Migration writes a fresh JSONL file alongside the legacy one.
        Assert.True(File.Exists(_dataPath));
        try { File.Delete(legacyPath); } catch { }
    }

    [Fact]
    public void Load_NoFile_ReturnsEmpty()
    {
        // No history file exists yet; LoadEvents should silently start empty.
        Assert.Empty(SyncHistory.GetRecent());
    }
}
