using System;
using System.Collections.Generic;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for the pure SyncHistory lookup helpers. These power the local short-circuit
/// that prevents redundant Letterboxd HTTP calls during sync.
/// </summary>
public class SyncHistoryLookupTests
{
    private const string User = "lachlan";
    private const string OtherUser = "thebarn";
    private const int FilmA = 100;
    private const int FilmB = 200;
    private static readonly DateTime ViewedToday = new DateTime(2026, 4, 30);
    private static readonly DateTime ViewedYesterday = new DateTime(2026, 4, 29);

    private static SyncEvent Event(string username, int tmdbId, DateTime viewingDate, SyncStatus status,
        DateTime? recordedAt = null)
        => new()
        {
            Username = username,
            TmdbId = tmdbId,
            ViewingDate = viewingDate,
            Status = status,
            Timestamp = recordedAt ?? DateTime.UtcNow
        };

    [Fact]
    public void WasSuccessfullySynced_TrueWhenSuccessEntryMatchesUserFilmAndDate()
    {
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Success)
        };

        Assert.True(SyncHistory.WasSuccessfullySynced(events, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_TrueForRewatchEntry()
    {
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Rewatch)
        };

        Assert.True(SyncHistory.WasSuccessfullySynced(events, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_FalseForFailedOrSkippedEntry()
    {
        // The whole point of prioritising these on the next run is that they DID NOT sync
        // successfully — they must not be treated as already-synced.
        var failedOrSkipped = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Failed),
            Event(User, FilmA, ViewedToday, SyncStatus.Skipped),
        };

        Assert.False(SyncHistory.WasSuccessfullySynced(failedOrSkipped, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_FalseWhenViewingDateDiffers()
    {
        // A successful sync for a previous viewing must not mask a new viewing on a different
        // date — the user expects the second viewing to land on Letterboxd as its own diary entry.
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedYesterday, SyncStatus.Success)
        };

        Assert.False(SyncHistory.WasSuccessfullySynced(events, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_IgnoresOtherUsers()
    {
        // SyncHistory is shared across users — make sure The Barn's sync doesn't think it
        // already happened just because lachlan synced the same film.
        var events = new List<SyncEvent>
        {
            Event(OtherUser, FilmA, ViewedToday, SyncStatus.Success)
        };

        Assert.False(SyncHistory.WasSuccessfullySynced(events, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_IgnoresOtherFilms()
    {
        var events = new List<SyncEvent>
        {
            Event(User, FilmB, ViewedToday, SyncStatus.Success)
        };

        Assert.False(SyncHistory.WasSuccessfullySynced(events, User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_FalseOnEmptyHistory()
    {
        Assert.False(SyncHistory.WasSuccessfullySynced(new List<SyncEvent>(), User, FilmA, ViewedToday));
    }

    [Fact]
    public void WasSuccessfullySynced_MatchesOnDateOnlyIgnoringTime()
    {
        // ViewingDate may be stored with a 00:00 time but the runner derives "today" from
        // DateTime.Now, which has a clock component. Comparison must be date-only.
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, new DateTime(2026, 4, 30, 0, 0, 0), SyncStatus.Success)
        };

        var withClockTime = new DateTime(2026, 4, 30, 14, 23, 17);
        Assert.True(SyncHistory.WasSuccessfullySynced(events, User, FilmA, withClockTime));
    }

    [Fact]
    public void GetLastStatusForFilm_ReturnsMostRecentByTimestamp()
    {
        // Behaviour we depend on for queue prioritisation: the most recent attempt's status
        // determines whether the film is treated as "previously failed" (priority 0).
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Failed,
                recordedAt: new DateTime(2026, 4, 1)),
            Event(User, FilmA, ViewedToday, SyncStatus.Success,
                recordedAt: new DateTime(2026, 4, 2)),
            Event(User, FilmA, ViewedToday, SyncStatus.Skipped,
                recordedAt: new DateTime(2026, 4, 3)),
        };

        Assert.Equal(SyncStatus.Skipped, SyncHistory.GetLastStatusForFilm(events, User, FilmA));
    }

    [Fact]
    public void GetLastStatusForFilm_NullWhenUserHasNoHistoryForFilm()
    {
        var events = new List<SyncEvent>
        {
            Event(OtherUser, FilmA, ViewedToday, SyncStatus.Success),
            Event(User, FilmB, ViewedToday, SyncStatus.Success),
        };

        Assert.Null(SyncHistory.GetLastStatusForFilm(events, User, FilmA));
    }

    [Fact]
    public void GetLastStatusForFilm_IgnoresOtherUsers()
    {
        var events = new List<SyncEvent>
        {
            Event(OtherUser, FilmA, ViewedToday, SyncStatus.Success,
                recordedAt: new DateTime(2026, 4, 5)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed,
                recordedAt: new DateTime(2026, 4, 1)),
        };

        // The newer event belongs to the other user — ours is older but is the right answer.
        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(events, User, FilmA));
    }
}
