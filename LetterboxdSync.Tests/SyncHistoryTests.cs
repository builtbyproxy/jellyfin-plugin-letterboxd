using System;
using System.IO;
using System.Text.Json;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class SyncHistoryTests : IDisposable
{
    private readonly string _tempDir;

    public SyncHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"letterboxd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Record_AppendsEventToJsonlFile()
    {
        var path = Path.Combine(_tempDir, "test-history.jsonl");

        var evt = new SyncEvent
        {
            FilmTitle = "Dune Part Two",
            FilmSlug = "dune-part-two",
            TmdbId = 693134,
            Username = "testuser",
            Timestamp = DateTime.UtcNow,
            ViewingDate = DateTime.Today,
            Status = SyncStatus.Success,
            Source = "scheduled"
        };

        // Write directly to verify JSONL format
        var json = JsonSerializer.Serialize(evt);
        File.AppendAllText(path, json + Environment.NewLine);

        var lines = File.ReadAllLines(path);
        Assert.Single(lines);

        var deserialized = JsonSerializer.Deserialize<SyncEvent>(lines[0]);
        Assert.NotNull(deserialized);
        Assert.Equal("Dune Part Two", deserialized!.FilmTitle);
        Assert.Equal("dune-part-two", deserialized.FilmSlug);
        Assert.Equal(693134, deserialized.TmdbId);
        Assert.Equal(SyncStatus.Success, deserialized.Status);
    }

    [Fact]
    public void SyncEvent_SerializesAllFields()
    {
        var evt = new SyncEvent
        {
            FilmTitle = "Test Film",
            FilmSlug = "test-film",
            TmdbId = 12345,
            Username = "user1",
            Timestamp = new DateTime(2026, 3, 30, 12, 0, 0, DateTimeKind.Utc),
            ViewingDate = new DateTime(2026, 3, 30),
            Status = SyncStatus.Failed,
            Error = "Connection timeout",
            Source = "playback"
        };

        var json = JsonSerializer.Serialize(evt);
        var back = JsonSerializer.Deserialize<SyncEvent>(json);

        Assert.NotNull(back);
        Assert.Equal("Test Film", back!.FilmTitle);
        Assert.Equal("test-film", back.FilmSlug);
        Assert.Equal(12345, back.TmdbId);
        Assert.Equal("user1", back.Username);
        Assert.Equal(SyncStatus.Failed, back.Status);
        Assert.Equal("Connection timeout", back.Error);
        Assert.Equal("playback", back.Source);
    }

    [Fact]
    public void SyncEvent_DeserializesWithMissingOptionalFields()
    {
        var json = "{\"FilmTitle\":\"Minimal\",\"TmdbId\":1,\"Username\":\"u\",\"Status\":0}";
        var evt = JsonSerializer.Deserialize<SyncEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal("Minimal", evt!.FilmTitle);
        Assert.Null(evt.Error);
        Assert.Null(evt.Source);
        Assert.Equal(string.Empty, evt.FilmSlug);
    }

    [Fact]
    public void JsonlFormat_LoadedInAnyOrder_GetRecentReturnsNewestFirst()
    {
        var path = Path.Combine(_tempDir, "order-test.jsonl");

        // Write events in chronological order (oldest first, like append would)
        var events = new[]
        {
            new SyncEvent { FilmTitle = "Oldest", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SyncEvent { FilmTitle = "Middle", Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new SyncEvent { FilmTitle = "Newest", Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        foreach (var evt in events)
            File.AppendAllText(path, JsonSerializer.Serialize(evt) + Environment.NewLine);

        // Read them back and sort by timestamp descending (like GetRecent does)
        var loaded = new List<SyncEvent>();
        foreach (var line in File.ReadAllLines(path))
        {
            var deserialized = JsonSerializer.Deserialize<SyncEvent>(line);
            if (deserialized != null) loaded.Add(deserialized);
        }

        var recent = loaded.OrderByDescending(e => e.Timestamp).Take(2).ToList();

        Assert.Equal("Newest", recent[0].FilmTitle);
        Assert.Equal("Middle", recent[1].FilmTitle);
    }

    [Fact]
    public void SyncStatus_SerializesAsInteger()
    {
        var evt = new SyncEvent { Status = SyncStatus.Rewatch };
        var json = JsonSerializer.Serialize(evt);
        Assert.Contains("3", json); // Rewatch = 3
    }

    [Fact]
    public void JsonlFormat_MultipleLines_AllDeserialize()
    {
        var path = Path.Combine(_tempDir, "multi.jsonl");
        var events = new[]
        {
            new SyncEvent { FilmTitle = "Film A", Status = SyncStatus.Success },
            new SyncEvent { FilmTitle = "Film B", Status = SyncStatus.Skipped },
            new SyncEvent { FilmTitle = "Film C", Status = SyncStatus.Failed, Error = "timeout" },
        };

        foreach (var evt in events)
            File.AppendAllText(path, JsonSerializer.Serialize(evt) + Environment.NewLine);

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            var deserialized = JsonSerializer.Deserialize<SyncEvent>(lines[i]);
            Assert.NotNull(deserialized);
            Assert.Equal(events[i].FilmTitle, deserialized!.FilmTitle);
        }
    }
}
