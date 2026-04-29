using System;
using System.Collections.Generic;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for SyncHistory.GetLastSuccessfulSyncDate (the lookup powering the local-history
/// duplicate backstop) combined with Helpers.IsRewatch (the threshold deciding whether the
/// new viewing is a genuine rewatch or a likely duplicate). The runner uses both together
/// before MarkAsWatched, so these tests verify the suppression decision matrix end to end.
/// </summary>
public class DuplicateBackstopTests
{
    private const string User = "lachlan";
    private const int FilmA = 100;
    private const int FilmB = 200;

    private static SyncEvent SuccessAt(DateTime viewingDate, DateTime? recordedAt = null, string username = User, int tmdbId = FilmA)
        => new()
        {
            Username = username,
            TmdbId = tmdbId,
            ViewingDate = viewingDate,
            Status = SyncStatus.Success,
            Timestamp = recordedAt ?? viewingDate.AddHours(1),
        };

    /// <summary>
    /// Returns true when the runner would suppress, i.e. there is prior sync history and
    /// IsRewatch says the new viewing is too soon to be a genuine rewatch.
    /// </summary>
    private static bool WouldSuppress(IEnumerable<SyncEvent> history, string username, int tmdbId, DateTime newViewingDate)
    {
        var last = SyncHistory.GetLastSuccessfulSyncDate(history, username, tmdbId);
        return last.HasValue && !Helpers.IsRewatch(last, newViewingDate);
    }

    [Fact]
    public void Suppresses_TwinlessCase_SameDateAsPriorSuccess()
    {
        // Real Letterboxd duplicate observed in Drvke97's diary and our own (Twinless on
        // 2026-03-25, logged 2x): the GetDiaryInfo Cloudflare-failure path let MarkAsWatched
        // create a second entry on the same date.
        var history = new List<SyncEvent> { SuccessAt(new DateTime(2026, 3, 25)) };

        Assert.True(WouldSuppress(history, User, FilmA, new DateTime(2026, 3, 25)));
    }

    [Fact]
    public void Suppresses_OneDayApart_AvatarMercyTrialPattern()
    {
        // Avatar / Mercy / Trial of the Chicago 7 all showed pairs one calendar day apart
        // with no rewatch flag. The IsRewatch threshold is strictly > 1 day, so consecutive
        // days suppress.
        var history = new List<SyncEvent> { SuccessAt(new DateTime(2026, 4, 28)) };

        Assert.True(WouldSuppress(history, User, FilmA, new DateTime(2026, 4, 29)));
    }

    [Fact]
    public void Allows_GenuineRewatchTwoDaysLater()
    {
        // Two clear days between viewings, treat as a rewatch and let MarkAsWatched proceed.
        var history = new List<SyncEvent> { SuccessAt(new DateTime(2026, 4, 28)) };

        Assert.False(WouldSuppress(history, User, FilmA, new DateTime(2026, 4, 30)));
    }

    [Fact]
    public void Allows_GenuineRewatchThreeWeeksLater_TreasurePlanetPattern()
    {
        // Treasure Planet was logged 2026-03-03 (rewatch) and again 2026-03-25 — clearly a
        // legitimate later viewing, must not be suppressed.
        var history = new List<SyncEvent> { SuccessAt(new DateTime(2026, 3, 3)) };

        Assert.False(WouldSuppress(history, User, FilmA, new DateTime(2026, 3, 25)));
    }

    [Fact]
    public void Allows_NeverSyncedFilm()
    {
        // Without prior history, nothing to suppress against; runner proceeds to MarkAsWatched.
        var history = new List<SyncEvent>();

        Assert.False(WouldSuppress(history, User, FilmA, new DateTime(2026, 4, 29)));
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_PrefersMostRecentByTimestamp()
    {
        // If multiple successful syncs exist (e.g. a real rewatch was correctly logged),
        // the backstop should compare against the most recent one, not an old one that
        // would falsely look like a rewatch threshold pass.
        var history = new List<SyncEvent>
        {
            SuccessAt(new DateTime(2026, 1, 1), recordedAt: new DateTime(2026, 1, 1, 12, 0, 0)),
            SuccessAt(new DateTime(2026, 4, 28), recordedAt: new DateTime(2026, 4, 28, 12, 0, 0)),
        };

        var last = SyncHistory.GetLastSuccessfulSyncDate(history, User, FilmA);

        Assert.Equal(new DateTime(2026, 4, 28), last);
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_IgnoresFailedAndSkipped()
    {
        // Backstop is about preventing ANOTHER successful entry from going to Letterboxd.
        // Failed/Skipped attempts are not entries on Letterboxd and must not block retries.
        var history = new List<SyncEvent>
        {
            new() { Username = User, TmdbId = FilmA, ViewingDate = new DateTime(2026, 4, 28),
                    Status = SyncStatus.Failed, Timestamp = DateTime.UtcNow },
            new() { Username = User, TmdbId = FilmA, ViewingDate = new DateTime(2026, 4, 28),
                    Status = SyncStatus.Skipped, Timestamp = DateTime.UtcNow },
        };

        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate(history, User, FilmA));
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_AcceptsRewatchAsValidPriorSuccess()
    {
        // Rewatch entries are real diary entries on Letterboxd and count as a prior sync
        // for the purposes of duplicate prevention.
        var history = new List<SyncEvent>
        {
            new() { Username = User, TmdbId = FilmA, ViewingDate = new DateTime(2026, 4, 1),
                    Status = SyncStatus.Rewatch, Timestamp = DateTime.UtcNow },
        };

        Assert.Equal(new DateTime(2026, 4, 1), SyncHistory.GetLastSuccessfulSyncDate(history, User, FilmA));
    }

    [Fact]
    public void GetLastSuccessfulSyncDate_IsScopedPerFilmAndPerUser()
    {
        var history = new List<SyncEvent>
        {
            SuccessAt(new DateTime(2026, 4, 28), tmdbId: FilmB),
            SuccessAt(new DateTime(2026, 4, 28), username: "thebarn"),
        };

        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate(history, User, FilmA));
    }
}
