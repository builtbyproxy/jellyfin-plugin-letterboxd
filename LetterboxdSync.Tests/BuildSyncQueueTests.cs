using System;
using System.Collections.Generic;
using System.Linq;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for LetterboxdSyncRunner.BuildSyncQueue — the pre-flight filter and prioritisation
/// that runs BEFORE any HTTP traffic. This is the heart of the issue #20 fix: skip what's
/// already synced locally, retry previously-failed/skipped films first.
/// </summary>
public class BuildSyncQueueTests
{
    private const string User = "lachlan";
    private static readonly DateTime Today = new DateTime(2026, 4, 30);

    private static (List<string> Queue, int LocallySkipped) Run(
        IEnumerable<(string Item, int? TmdbId, DateTime ViewingDate)> candidates,
        Func<string, int, DateTime, bool>? wasSynced = null,
        Func<string, int, SyncStatus?>? lastStatus = null)
    {
        return LetterboxdSyncRunner.BuildSyncQueue(
            candidates,
            User,
            wasSynced ?? ((_, _, _) => false),
            lastStatus ?? ((_, _) => null));
    }

    [Fact]
    public void DropsAlreadySyncedFilmsAndCountsThem()
    {
        var candidates = new[]
        {
            (Item: "alreadySynced", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "fresh", TmdbId: (int?)200, ViewingDate: Today),
        };

        var (queue, skipped) = Run(candidates, wasSynced: (_, tid, _) => tid == 100);

        Assert.Equal(new[] { "fresh" }, queue);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void PreviouslyFailedFilmsComeFirst()
    {
        // The point of the prioritisation: if Letterboxd starts blocking part-way, we make
        // progress on the backlog rather than re-attempting fresh films at the head of queue.
        var candidates = new[]
        {
            (Item: "fresh1", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "previouslyFailed", TmdbId: (int?)200, ViewingDate: Today),
            (Item: "fresh2", TmdbId: (int?)300, ViewingDate: Today),
        };

        var (queue, _) = Run(candidates,
            lastStatus: (_, tid) => tid == 200 ? SyncStatus.Failed : null);

        Assert.Equal("previouslyFailed", queue[0]);
        Assert.Contains("fresh1", queue);
        Assert.Contains("fresh2", queue);
    }

    [Fact]
    public void PreviouslySkippedFilmsAlsoPrioritised()
    {
        // Skipped (e.g. from a transient duplicate-check error) is also an unfinished attempt
        // that deserves priority on the next run.
        var candidates = new[]
        {
            (Item: "fresh", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "previouslySkipped", TmdbId: (int?)200, ViewingDate: Today),
        };

        var (queue, _) = Run(candidates,
            lastStatus: (_, tid) => tid == 200 ? SyncStatus.Skipped : null);

        Assert.Equal("previouslySkipped", queue[0]);
        Assert.Equal("fresh", queue[1]);
    }

    [Fact]
    public void PreviouslySuccessfulButDifferentDateIsTreatedAsFresh()
    {
        // wasSynced returns false because the date doesn't match (rewatch on a new date),
        // and lastStatus is Success — that's neither Failed nor Skipped, so the film stays
        // at the regular "never attempted" priority.
        var candidates = new[]
        {
            (Item: "rewatch", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "fresh", TmdbId: (int?)200, ViewingDate: Today),
        };

        var (queue, _) = Run(candidates,
            wasSynced: (_, _, _) => false,
            lastStatus: (_, tid) => tid == 100 ? SyncStatus.Success : null);

        // Both at priority 1 — order should be preserved (stable sort).
        Assert.Equal(new[] { "rewatch", "fresh" }, queue);
    }

    [Fact]
    public void FilmsWithoutTmdbIdStayInQueue()
    {
        // The main loop logs them and records an explicit "No TMDb ID" skip event, so we
        // mustn't drop them silently here.
        var candidates = new[]
        {
            (Item: "noTmdb", TmdbId: (int?)null, ViewingDate: Today),
            (Item: "withTmdb", TmdbId: (int?)100, ViewingDate: Today),
        };

        var (queue, skipped) = Run(candidates);

        Assert.Equal(2, queue.Count);
        Assert.Equal(0, skipped);
        Assert.Contains("noTmdb", queue);
    }

    [Fact]
    public void EmptyInputGivesEmptyQueue()
    {
        var (queue, skipped) = Run(Array.Empty<(string, int?, DateTime)>());

        Assert.Empty(queue);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void AllAlreadySyncedYieldsEmptyQueue()
    {
        var candidates = new[]
        {
            (Item: "a", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "b", TmdbId: (int?)200, ViewingDate: Today),
        };

        var (queue, skipped) = Run(candidates, wasSynced: (_, _, _) => true);

        Assert.Empty(queue);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void PassesUsernameThroughToCallbacks()
    {
        // Defense against a regression where the runner accidentally hardcodes a username
        // or passes the Letterboxd username instead of the Jellyfin one.
        string? observedWasSyncedUser = null;
        string? observedLastStatusUser = null;

        var candidates = new[]
        {
            (Item: "x", TmdbId: (int?)100, ViewingDate: Today),
        };

        Run(candidates,
            wasSynced: (u, _, _) => { observedWasSyncedUser = u; return false; },
            lastStatus: (u, _) => { observedLastStatusUser = u; return null; });

        Assert.Equal(User, observedWasSyncedUser);
        Assert.Equal(User, observedLastStatusUser);
    }

    [Fact]
    public void PreservesFreshFilmOrderWhenNoPriorityDifference()
    {
        // Stable ordering matters for predictable user-facing logs and tests.
        var candidates = new[]
        {
            (Item: "first", TmdbId: (int?)100, ViewingDate: Today),
            (Item: "second", TmdbId: (int?)200, ViewingDate: Today),
            (Item: "third", TmdbId: (int?)300, ViewingDate: Today),
        };

        var (queue, _) = Run(candidates);

        Assert.Equal(new[] { "first", "second", "third" }, queue);
    }
}
