using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Performs Letterboxd diary sync. Used by both the scheduled task and the user-triggered
/// API endpoint, with a global semaphore so SyncProgress can only be driven by one caller
/// at a time.
/// </summary>
public class LetterboxdSyncRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LetterboxdSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public LetterboxdSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LetterboxdSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    public static bool IsRunning => SyncGate.IsRunning;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, string source, CancellationToken cancellationToken)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Sync already running, skipping scheduled run");
            return;
        }

        try
        {
            // Fan out across every (user, account) pair. Each account's sync is
            // independent so one failing or rate-limited account never blocks the others.
            var pairs = _userManager.Users
                .SelectMany(u => Config.GetEnabledAccountsForUser(u.Id.ToString("N"))
                    .Select(a => (User: u, Account: a)))
                .ToList();

            var processed = 0;
            foreach (var (user, account) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                processed++;
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    /// <summary>
    /// Run sync for a single user. When letterboxdUsername is null/empty, fan out
    /// across all enabled accounts for the user. Otherwise target only that account.
    /// Returns false if another sync is already running, the user is unknown, or
    /// they have no matching enabled account.
    /// </summary>
    public async Task<bool> TryRunForUserAsync(
        string userJellyfinId,
        string source,
        IProgress<double> progress,
        CancellationToken cancellationToken,
        string? letterboxdUsername = null)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Sync already running, refusing user-triggered start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            var user = _userManager.Users.FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found, cannot start sync", userJellyfinId);
                return false;
            }

            List<Account> accounts;
            if (!string.IsNullOrEmpty(letterboxdUsername))
            {
                var single = Config.FindAccount(userJellyfinId, letterboxdUsername);
                if (single == null)
                {
                    _logger.LogWarning("No enabled Letterboxd account {LbUser} for {Username}, cannot start sync",
                        letterboxdUsername, user.Username);
                    return false;
                }
                accounts = new List<Account> { single };
            }
            else
            {
                accounts = Config.GetEnabledAccountsForUser(userJellyfinId).ToList();
                if (accounts.Count == 0)
                {
                    _logger.LogWarning("No enabled Letterboxd accounts for {Username}, cannot start sync", user.Username);
                    return false;
                }
            }

            var processed = 0;
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                processed++;
                progress.Report((double)processed / accounts.Count * 100);
            }
            return true;
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    private async Task SyncOneUserAsync(User user, Account account, string source, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Letterboxd sync for {Username} (source={Source})", user.Username, source);
        SyncProgress.Start("Letterboxd Sync", "Authenticating");

        List<BaseItem> movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            IsPlayed = true,
        }).ToList();

        if (movies.Count == 0)
        {
            SyncProgress.Complete();
            return;
        }

        if (account.EnableDateFilter)
        {
            var cutoff = DateTime.UtcNow.AddDays(-account.DateFilterDays);
            movies = movies.Where(m =>
            {
                var ud = _userDataManager.GetUserData(user, m);
                return ud?.LastPlayedDate.HasValue == true && ud.LastPlayedDate!.Value >= cutoff;
            }).ToList();
        }

        if (movies.Count == 0)
        {
            SyncProgress.Complete();
            return;
        }

        // Filter out anything we already successfully synced for this exact viewing date,
        // so we don't burn Cloudflare quota re-checking films that are definitely on Letterboxd.
        // Sort what's left so previously-failed/skipped films come first — if rate limits hit,
        // we make progress on the backlog instead of repeatedly retrying the same head of queue.
        int preFilterCount = movies.Count;
        int locallySkipped = 0;

        if (account.SkipPreviouslySynced)
        {
            var candidates = movies.Select(m =>
            {
                int? tid = int.TryParse(m.GetProviderId(MetadataProvider.Tmdb), out var v) ? v : null;
                var ud = _userDataManager.GetUserData(user, m);
                var viewing = ud?.LastPlayedDate?.Date ?? DateTime.Now.Date;
                return (Item: m, TmdbId: tid, ViewingDate: viewing);
            });

            var (queue, skippedLocally) = BuildSyncQueue(
                candidates,
                user.Username ?? string.Empty,
                SyncHistory.WasSuccessfullySynced,
                SyncHistory.GetLastStatusForFilm);
            movies = queue;
            locallySkipped = skippedLocally;
        }

        if (locallySkipped > 0)
            _logger.LogInformation("Skipping {Count} of {Total} films for {Username}: already in local sync history",
                locallySkipped, preFilterCount, user.Username);

        if (movies.Count == 0)
        {
            SyncProgress.Complete();
            return;
        }

        ILetterboxdService service;
        try
        {
            service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Auth failed for {Username}: {Message}", user.Username, ex.Message);
            SyncProgress.Complete();
            return;
        }

        using var _ = service;

        var synced = 0;
        var skipped = 0;
        var failed = 0;

        SyncProgress.SetPhase("Syncing films");
        SyncProgress.SetTotal(movies.Count);

        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tmdbIdStr = movie.GetProviderId(MetadataProvider.Tmdb);
            if (!int.TryParse(tmdbIdStr, out var tmdbId))
            {
                _logger.LogInformation("Skipping {Title}: no TMDb ID on the Jellyfin item", movie.Name);
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = movie.Name, Username = user.Username ?? string.Empty, Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Skipped, Error = "No TMDb ID", Source = source
                });
                skipped++;
                continue;
            }

            try
            {
                var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                await Task.Delay(3000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                var userData = _userDataManager.GetUserData(user, movie);
                var viewingDate = userData?.LastPlayedDate?.Date ?? DateTime.Now.Date;

                var diaryInfo = await service.GetDiaryInfoAsync(film.FilmId, account.LetterboxdUsername).ConfigureAwait(false);
                if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
                {
                    _logger.LogInformation("Skipping {Title} (TMDb:{TmdbId}): already on Letterboxd diary for {Date}",
                        movie.Name, tmdbId, viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                        Username = user.Username ?? string.Empty, Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate, Status = SyncStatus.Skipped,
                        Error = "Already on Letterboxd diary for this date", Source = source
                    });
                    skipped++;
                    SyncProgress.IncrementProcessed();
                    continue;
                }

                // Local-history backstop. The Letterboxd-side IsDuplicate check above silently
                // returns false when GetDiaryInfo fails (e.g. Cloudflare 403 -> null lastDate),
                // which used to let MarkAsWatched create a duplicate entry. Cross-check our own
                // append-only log: if we have a recent successful sync that is not far enough
                // back to count as a real rewatch, refuse rather than risk a duplicate.
                var localLastSync = SyncHistory.GetLastSuccessfulSyncDate(user.Username ?? string.Empty, tmdbId);
                if (localLastSync.HasValue && !Helpers.IsRewatch(localLastSync, viewingDate))
                {
                    var lastSyncStr = localLastSync.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    _logger.LogInformation("Skipping {Title} (TMDb:{TmdbId}): local history shows prior sync on {Date}, suppressing potential duplicate",
                        movie.Name, tmdbId, lastSyncStr);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                        Username = user.Username ?? string.Empty, Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate, Status = SyncStatus.Skipped,
                        Error = $"Local history shows prior sync on {lastSyncStr}, suppressing potential duplicate",
                        Source = source
                    });
                    skipped++;
                    SyncProgress.IncrementProcessed();
                    continue;
                }

                bool isRewatch = false;
                bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);
                double? lbRating = Helpers.MapRating(userData?.Rating);

                await service.MarkAsWatchedAsync(film.Slug, film.FilmId, userData?.LastPlayedDate, liked,
                    film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

                _logger.LogInformation("Logged {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} on {Date}",
                    movie.Name, tmdbId, user.Username,
                    viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = movie.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                    Username = user.Username ?? string.Empty, Timestamp = DateTime.UtcNow,
                    ViewingDate = viewingDate, Status = SyncStatus.Success, Source = source
                });
                synced++;
                SyncProgress.IncrementProcessed();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) for {Username}: {Message}",
                    movie.Name, tmdbId, user.Username, ex.Message);
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = movie.Name, TmdbId = tmdbId,
                    Username = user.Username ?? string.Empty, Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed, Error = ex.Message, Source = source
                });
                failed++;
                SyncProgress.IncrementProcessed();

                if (account.StopOnFailure)
                {
                    _logger.LogWarning("Stop-on-failure enabled for {Username}: halting after {Synced} synced, {Skipped} skipped, {Failed} failed",
                        user.Username, synced, skipped, failed);
                    break;
                }
            }
        }

        _logger.LogInformation("Letterboxd sync complete for {Username}: {Synced} synced, {Skipped} skipped (+{LocalSkipped} skipped locally), {Failed} failed",
            user.Username, synced, skipped, locallySkipped, failed);
        SyncProgress.Complete();
    }

    /// <summary>
    /// Filter out already-synced candidates and order the rest with previously-failed/skipped first,
    /// never-attempted next. Items with no TMDb ID stay in the queue at the lowest priority so the
    /// main loop can record an explicit skip event with reason. Pure function for testability.
    /// </summary>
    internal static (List<T> Queue, int LocallySkipped) BuildSyncQueue<T>(
        IEnumerable<(T Item, int? TmdbId, DateTime ViewingDate)> candidates,
        string username,
        Func<string, int, DateTime, bool> wasSynced,
        Func<string, int, SyncStatus?> lastStatus)
    {
        var remaining = new List<(T Item, int Priority)>();
        int locallySkipped = 0;

        foreach (var c in candidates)
        {
            if (c.TmdbId is not int tid)
            {
                // No TMDb ID — main loop logs and records an explicit skip event with reason.
                remaining.Add((c.Item, 1));
                continue;
            }

            if (wasSynced(username, tid, c.ViewingDate))
            {
                locallySkipped++;
                continue;
            }

            var prev = lastStatus(username, tid);
            var priority = (prev == SyncStatus.Failed || prev == SyncStatus.Skipped) ? 0 : 1;
            remaining.Add((c.Item, priority));
        }

        return (remaining.OrderBy(x => x.Priority).Select(x => x.Item).ToList(), locallySkipped);
    }
}
