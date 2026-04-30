using System;
using System.Collections.Generic;
using System.Linq;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for SyncHistory.GetPage. Powers the dashboard paginator added in 1.9.1
/// after we removed the 500-entry cap.
/// </summary>
public class SyncHistoryPaginationTests
{
    private static List<SyncEvent> Events(int n, string username = "lachlan")
    {
        var list = new List<SyncEvent>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new SyncEvent
            {
                FilmTitle = $"Film {i:D3}",
                TmdbId = 1000 + i,
                Username = username,
                Status = SyncStatus.Success,
                // Older index = older timestamp, so newest-first ordering puts the highest indexes first.
                Timestamp = new DateTime(2026, 1, 1).AddMinutes(i),
            });
        }
        return list;
    }

    [Fact]
    public void GetPage_ReturnsNewestFirst()
    {
        var events = Events(5);

        var (slice, total) = SyncHistory.GetPage(events, offset: 0, count: 3);

        Assert.Equal(5, total);
        Assert.Equal(new[] { "Film 004", "Film 003", "Film 002" }, slice.Select(e => e.FilmTitle));
    }

    [Fact]
    public void GetPage_OffsetSkipsFromTheTopOfNewestFirst()
    {
        var events = Events(10);

        var (slice, total) = SyncHistory.GetPage(events, offset: 3, count: 2);

        Assert.Equal(10, total);
        // Sorted newest-first: Film 009, 008, 007, then offset 3 lands on Film 006, 005.
        Assert.Equal(new[] { "Film 006", "Film 005" }, slice.Select(e => e.FilmTitle));
    }

    [Fact]
    public void GetPage_OffsetPastEndYieldsEmptySlice()
    {
        var events = Events(10);

        var (slice, total) = SyncHistory.GetPage(events, offset: 50, count: 10);

        Assert.Empty(slice);
        Assert.Equal(10, total);
    }

    [Fact]
    public void GetPage_NegativeOffsetTreatedAsZero()
    {
        var events = Events(5);

        var (slice, total) = SyncHistory.GetPage(events, offset: -10, count: 2);

        Assert.Equal(5, total);
        Assert.Equal(new[] { "Film 004", "Film 003" }, slice.Select(e => e.FilmTitle));
    }

    [Fact]
    public void GetPage_NegativeCountYieldsEmptySlice()
    {
        var events = Events(5);

        var (slice, total) = SyncHistory.GetPage(events, offset: 0, count: -1);

        Assert.Empty(slice);
        Assert.Equal(5, total);
    }

    [Fact]
    public void GetPage_TotalReflectsUserFilter()
    {
        var events = new List<SyncEvent>();
        events.AddRange(Events(7, "lachlan"));
        events.AddRange(Events(3, "thebarn"));

        var (slice, total) = SyncHistory.GetPage(events, offset: 0, count: 100, username: "thebarn");

        Assert.Equal(3, total);
        Assert.Equal(3, slice.Count);
        Assert.All(slice, e => Assert.Equal("thebarn", e.Username));
    }

    [Fact]
    public void GetPage_NoUsernameFilterReturnsAllUsersInOrder()
    {
        var events = new List<SyncEvent>
        {
            new() { FilmTitle = "A", Username = "lachlan", Timestamp = new DateTime(2026, 1, 1) },
            new() { FilmTitle = "B", Username = "thebarn", Timestamp = new DateTime(2026, 1, 2) },
            new() { FilmTitle = "C", Username = "lachlan", Timestamp = new DateTime(2026, 1, 3) },
        };

        var (slice, total) = SyncHistory.GetPage(events, offset: 0, count: 100);

        Assert.Equal(3, total);
        Assert.Equal(new[] { "C", "B", "A" }, slice.Select(e => e.FilmTitle));
    }
}
