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

    private static SyncEvent DiaryImportEvent(string username, int tmdbId, DateTime? recordedAt = null)
        => new()
        {
            Username = username,
            TmdbId = tmdbId,
            Status = SyncStatus.Skipped,
            Source = SyncEventSources.DiaryImport,
            Timestamp = recordedAt ?? DateTime.UtcNow
        };

    [Fact]
    public void WasImportedFromDiary_TrueWhenMatchingDiaryImportEventExists()
    {
        var events = new List<SyncEvent> { DiaryImportEvent(User, FilmA) };

        Assert.True(SyncHistory.WasImportedFromDiary(events, User, FilmA));
    }

    [Fact]
    public void WasImportedFromDiary_FalseWhenSourceIsNotDiaryImport()
    {
        // Only the diary-import sentinel counts; ordinary sync events are not import markers.
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Success)
        };

        Assert.False(SyncHistory.WasImportedFromDiary(events, User, FilmA));
    }

    [Fact]
    public void WasImportedFromDiary_IgnoresOtherUsersAndOtherFilms()
    {
        var events = new List<SyncEvent>
        {
            DiaryImportEvent(OtherUser, FilmA),
            DiaryImportEvent(User, FilmB)
        };

        Assert.False(SyncHistory.WasImportedFromDiary(events, User, FilmA));
    }

    [Fact]
    public void WasImportedFromDiary_FalseOnEmptyHistory()
    {
        Assert.False(SyncHistory.WasImportedFromDiary(new List<SyncEvent>(), User, FilmA));
    }

    [Fact]
    public void WasImportedFromDiary_TrueWhenAnyHistoricalDiaryImportExists()
    {
        // Even if subsequent attempts produce other events, the historical import marker
        // should keep the runner from re-exporting until a real Jellyfin playback happens
        // (which the runner gates separately via LastPlayedDate).
        var events = new List<SyncEvent>
        {
            DiaryImportEvent(User, FilmA, recordedAt: new DateTime(2026, 4, 1)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 2))
        };

        Assert.True(SyncHistory.WasImportedFromDiary(events, User, FilmA));
    }

    [Fact]
    public void GetConsecutiveFailureCount_ZeroOnEmptyHistory()
    {
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(new List<SyncEvent>(), User, FilmA));
    }

    [Fact]
    public void GetConsecutiveFailureCount_CountsTrailingFailures()
    {
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 1)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 2)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 3)),
        };

        Assert.Equal(3, SyncHistory.GetConsecutiveFailureCount(events, User, FilmA));
    }

    [Fact]
    public void GetConsecutiveFailureCount_StopsAtMostRecentNonFailure()
    {
        // A later success breaks the streak: only failures after it count, and here there
        // are none, so the film is no longer considered a repeat-failure.
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 1)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 2)),
            Event(User, FilmA, ViewedToday, SyncStatus.Success, recordedAt: new DateTime(2026, 4, 3)),
        };

        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(events, User, FilmA));
    }

    [Fact]
    public void GetConsecutiveFailureCount_CountsOnlyFailuresAfterLastSuccess()
    {
        // Failures, then a success that resets the streak, then fresh failures: only the
        // post-success failures count toward abandoning the film.
        var events = new List<SyncEvent>
        {
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 1)),
            Event(User, FilmA, ViewedToday, SyncStatus.Success, recordedAt: new DateTime(2026, 4, 2)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 3)),
            Event(User, FilmA, ViewedToday, SyncStatus.Failed, recordedAt: new DateTime(2026, 4, 4)),
        };

        Assert.Equal(2, SyncHistory.GetConsecutiveFailureCount(events, User, FilmA));
    }

    [Fact]
    public void GetConsecutiveFailureCount_IgnoresOtherUsersAndFilms()
    {
        var events = new List<SyncEvent>
        {
            Event(OtherUser, FilmA, ViewedToday, SyncStatus.Failed),
            Event(User, FilmB, ViewedToday, SyncStatus.Failed),
        };

        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(events, User, FilmA));
    }
}
