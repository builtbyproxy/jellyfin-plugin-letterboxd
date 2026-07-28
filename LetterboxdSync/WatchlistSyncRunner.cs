using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Performs the Letterboxd-watchlist → Jellyfin-playlist → Seerr chain. Used by both
/// the scheduled <see cref="WatchlistSyncTask"/> and the user-triggered API endpoint, gated
/// behind <see cref="SyncGate"/> so it serialises with the diary sync.
/// </summary>
public class WatchlistSyncRunner
{
    private readonly ILogger<WatchlistSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    // activityManager is optional so existing construction sites (and tests) keep
    // working; a null only skips the auth-breaker admin notification.
    public WatchlistSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPlaylistManager playlistManager,
        MediaBrowser.Model.Activity.IActivityManager? activityManager = null)
    {
        _activityManager = activityManager;
        _logger = loggerFactory.CreateLogger<WatchlistSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _playlistManager = playlistManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, string source, CancellationToken cancellationToken)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Another sync is already running, skipping scheduled watchlist run");
            return;
        }

        try
        {
            using var jellyseerr = CreateJellyseerrClient();

            var pairs = _userManager.GetUsers()
                .SelectMany(u => Config.GetEnabledAccountsForUser(u.Id.ToString("N"))
                    .Where(a => a.EnableWatchlistSync)
                    .Select(a => (User: u, Account: a)))
                .ToList();

            SyncProgress.Start(SyncProgress.TrackLetterboxd, "Letterboxd Watchlist", "Starting");

            var processed = 0;
            foreach (var (user, account) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, jellyseerr, source, cancellationToken).ConfigureAwait(false);
                processed++;
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    /// <summary>
    /// Run watchlist sync for a single user. When letterboxdUsername is null/empty,
    /// fans out across every enabled-with-watchlist account for that user. Otherwise
    /// targets only the named account. Returns false if another sync is already
    /// running, the user is unknown, or they have no matching account.
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
            _logger.LogWarning("Another sync is already running, refusing user-triggered watchlist start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found, cannot start watchlist sync", userJellyfinId);
                return false;
            }

            var enabled = Config.GetEnabledAccountsForUser(userJellyfinId)
                .Where(a => a.EnableWatchlistSync)
                .ToList();

            List<Account> accounts;
            if (!string.IsNullOrEmpty(letterboxdUsername))
            {
                var single = enabled.FirstOrDefault(a => string.Equals(
                    a.LetterboxdUsername, letterboxdUsername, StringComparison.OrdinalIgnoreCase));
                if (single == null)
                {
                    _logger.LogWarning("No enabled watchlist-sync account {LbUser} for {Username}",
                        letterboxdUsername, user.Username);
                    return false;
                }
                accounts = new List<Account> { single };
            }
            else
            {
                accounts = enabled;
                if (accounts.Count == 0)
                {
                    _logger.LogWarning("No enabled watchlist-sync accounts for {Username}", user.Username);
                    return false;
                }
            }

            using var jellyseerr = CreateJellyseerrClient();
            SyncProgress.Start(SyncProgress.TrackLetterboxd, "Letterboxd Watchlist", "Starting");

            var processed = 0;
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, jellyseerr, source, cancellationToken).ConfigureAwait(false);
                processed++;
                progress.Report((double)processed / accounts.Count * 100);
            }
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return true;
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    /// <summary>
    /// Test-only override. When set, CreateJellyseerrClient delegates to this so tests
    /// can inject a SeerrClient bound to a mock HttpMessageHandler. Production
    /// never assigns it.
    /// </summary>
    internal static Func<string, string, ILogger, SeerrClient?>? JellyseerrClientFactoryOverride;

    private SeerrClient? CreateJellyseerrClient()
    {
        if (!SeerrClient.IsConfigured(Config.JellyseerrUrl, Config.JellyseerrApiKey))
            return null;

        if (JellyseerrClientFactoryOverride != null)
            return JellyseerrClientFactoryOverride(Config.JellyseerrUrl!, Config.JellyseerrApiKey!, _logger);

        return new SeerrClient(Config.JellyseerrUrl!, Config.JellyseerrApiKey!, _logger);
    }

    private async Task SyncOneUserAsync(User user, Account account, SeerrClient? jellyseerr, string source, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting watchlist sync for {Username} (source={Source})", user.Username, source);
        SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, $"Authenticating {user.Username}");

        var breakerUserId = user.Id.ToString("N");
        if (AuthBreaker.IsOpen(breakerUserId, account.LetterboxdUsername))
        {
            _logger.LogInformation(
                "Skipping watchlist sync for {Username}: auth breaker open; re-save credentials to resume",
                account.LetterboxdUsername);
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
            // No SyncEvent is recorded on this early-exit path; hook telemetry directly.
            TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
            if (AuthBreaker.RecordFailure(breakerUserId, account.LetterboxdUsername, ex.Message))
                await AuthBreaker.NotifyOpenedAsync(_activityManager, user.Id, account.LetterboxdUsername, _logger).ConfigureAwait(false);
            return;
        }

        AuthBreaker.RecordSuccess(breakerUserId, account.LetterboxdUsername);

        using var _s = service;

        SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, $"Fetching watchlist for {user.Username}");
        List<int> tmdbIds;
        try
        {
            tmdbIds = await service.GetWatchlistTmdbIdsAsync(account.LetterboxdUsername).ConfigureAwait(false);
            _logger.LogInformation("Found {Count} films in {Username}'s Letterboxd watchlist",
                tmdbIds.Count, account.LetterboxdUsername);
            WatchlistStats.SetFilm(user.Id.ToString("N"), account.LetterboxdUsername, tmdbIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to fetch watchlist for {Username}: {Message}", user.Username, ex.Message);
            TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
            return;
        }

        SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, $"Updating Jellyfin playlist for {user.Username}");
        var allMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        });

        var watchlistItemIds = new HashSet<Guid>();
        var matchedTmdbIds = new HashSet<int>();
        foreach (var tmdbId in tmdbIds)
        {
            var match = allMovies.FirstOrDefault(m =>
                m.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());

            if (match != null)
            {
                watchlistItemIds.Add(match.Id);
                matchedTmdbIds.Add(tmdbId);
            }
        }

        _logger.LogInformation("Matched {Matched}/{Total} watchlist films to Jellyfin library",
            watchlistItemIds.Count, tmdbIds.Count);

        await UpdatePlaylistAsync(user, account, watchlistItemIds, tmdbIds.Count).ConfigureAwait(false);

        // Seerr integration: auto-request unmatched films and/or mirror the
        // Letterboxd watchlist into the user's Seerr watchlist.
        var jellyseerrWanted = jellyseerr != null && (account.AutoRequestWatchlist || account.MirrorJellyseerrWatchlist);
        if (!jellyseerrWanted) return;

        int? jellyseerrUserId;
        try
        {
            jellyseerrUserId = await jellyseerr!.GetJellyseerrUserIdAsync(account.UserJellyfinId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not fetch Seerr user map for {Username}: {Message}",
                user.Username, ex.Message);
            return;
        }

        if (jellyseerrUserId == null)
        {
            _logger.LogWarning("No Seerr user linked to Jellyfin user {Username}; skipping Seerr sync",
                user.Username);
            return;
        }

        if (account.MirrorJellyseerrWatchlist)
        {
            // The Seerr watchlist is keyed by Jellyfin user, not by Letterboxd
            // account: running the mirror once per account would have each account
            // overwrite the previous one's diff (toRemove = Seerr - thisAccount
            // wipes the other accounts' films). Only the primary account owns the
            // Seerr-watchlist destination so two accounts on one Jellyfin user
            // can't clobber each other.
            var primary = Config.GetPrimaryAccountForUser(account.UserJellyfinId);
            if (ReferenceEquals(primary, account))
            {
                SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, $"Mirroring Seerr watchlist for {user.Username}");
                await MirrorJellyseerrWatchlistAsync(jellyseerr!, jellyseerrUserId.Value, tmdbIds, user.Username!, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "Skipping Seerr watchlist mirror for {LbUser}: not the primary account for {Username}",
                    account.LetterboxdUsername, user.Username);
            }
        }

        if (account.AutoRequestWatchlist)
        {
            // Default: only films missing from the library. Backfill mode: the whole watchlist,
            // so already-available films also get an attributed request (per-user dedup happens
            // inside RequestMovieAsync, which skips titles this user already requested).
            var requestIds = account.BackfillAvailableRequests
                ? tmdbIds
                : tmdbIds.Where(id => !matchedTmdbIds.Contains(id)).ToList();

            SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, $"Requesting {(account.BackfillAvailableRequests ? "watchlist" : "missing")} films via Seerr for {user.Username}");
            if (requestIds.Count == 0) return;

            var requested = 0;
            var alreadyExists = 0;
            var failed = 0;
            foreach (var tmdbId in requestIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (result, title) = await jellyseerr!.RequestMovieAsync(tmdbId, jellyseerrUserId.Value, account.BackfillAvailableRequests).ConfigureAwait(false);
                    switch (result)
                    {
                        case SeerrClient.RequestResult.Requested:
                            requested++;
                            SyncHistory.Record(new SyncEvent
                            {
                                FilmTitle = title ?? $"TMDb {tmdbId}",
                                TmdbId = tmdbId,
                                Username = user.Username!,
                                Timestamp = DateTime.UtcNow,
                                Status = SyncStatus.Requested,
                                Source = SyncEventSources.SeerrAutoRequestFilm
                            });
                            break;
                        case SeerrClient.RequestResult.AlreadyExists:
                            alreadyExists++;
                            break;
                        default:
                            failed++;
                            SyncHistory.Record(new SyncEvent
                            {
                                FilmTitle = title ?? $"TMDb {tmdbId}",
                                TmdbId = tmdbId,
                                Username = user.Username!,
                                Timestamp = DateTime.UtcNow,
                                Status = SyncStatus.Failed,
                                Source = SyncEventSources.SeerrAutoRequestFilm,
                                Error = $"Seerr request failed for TMDb {tmdbId}"
                            });
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Seerr request errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                    failed++;
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = $"TMDb {tmdbId}",
                        TmdbId = tmdbId,
                        Username = user.Username!,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Source = SyncEventSources.SeerrAutoRequestFilm,
                        Error = ex.Message
                    });
                }
            }
            _logger.LogInformation(
                "Seerr auto-request for {Username} ({Mode}): {Requested} new, {Existing} already on Seerr, {Failed} failed of {Total} considered",
                user.Username, account.BackfillAvailableRequests ? "backfill" : "unmatched-only",
                requested, alreadyExists, failed, requestIds.Count);

            // Seerr failures surface as return values, not SyncEvents; count the
            // batch once (not per film) so one outage doesn't inflate the error counter.
            if (failed > 0)
                TelemetryService.RecordError(TelemetryService.CatJellyseerr);
        }
    }

    private async Task UpdatePlaylistAsync(User user, Account account, HashSet<Guid> watchlistItemIds, int letterboxdCount)
    {
        // Per-account playlist name so users with multiple Letterboxd accounts on
        // one Jellyfin user (e.g. shared TV login) each get their own playlist.
        // Defaults to "Letterboxd Watchlist ({letterboxdUsername})" or the override.
        var playlistName = account.GetPlaylistName();

        // letterboxdCount == 0 means the SCRAPE came back empty (e.g. Cloudflare blocked us),
        // not "watchlistItemIds is empty because none of the watchlist matched the library" -
        // those are different signals, so this is passed explicitly rather than re-derived
        // from watchlistItemIds.Count.
        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, _logger, user, playlistName, watchlistItemIds,
            sourceWasEmpty: letterboxdCount == 0).ConfigureAwait(false);
    }

    private async Task MirrorJellyseerrWatchlistAsync(
        SeerrClient jellyseerr,
        int jellyseerrUserId,
        List<int> letterboxdTmdbIds,
        string jellyfinUsername,
        CancellationToken cancellationToken)
    {
        if (letterboxdTmdbIds.Count == 0)
        {
            _logger.LogWarning(
                "Empty Letterboxd watchlist for {Username}; skipping Seerr mirror to avoid mass-deletion",
                jellyfinUsername);
            return;
        }

        HashSet<int> currentSeerr;
        try
        {
            currentSeerr = await jellyseerr.GetUserWatchlistTmdbIdsAsync(jellyseerrUserId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch Seerr watchlist for {Username}: {Message}",
                jellyfinUsername, ex.Message);
            return;
        }

        var letterboxdSet = new HashSet<int>(letterboxdTmdbIds);
        var toAdd = letterboxdSet.Where(id => !currentSeerr.Contains(id)).ToList();
        var toRemove = currentSeerr.Where(id => !letterboxdSet.Contains(id)).ToList();

        var added = 0;
        var addFailed = 0;
        foreach (var tmdbId in toAdd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await jellyseerr.AddToWatchlistAsync(tmdbId, jellyseerrUserId).ConfigureAwait(false))
                    added++;
                else
                    addFailed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Seerr watchlist add errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                addFailed++;
            }
        }

        var removed = 0;
        var removeFailed = 0;
        foreach (var tmdbId in toRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await jellyseerr.RemoveFromWatchlistAsync(tmdbId, jellyseerrUserId).ConfigureAwait(false))
                    removed++;
                else
                    removeFailed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Seerr watchlist remove errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                removeFailed++;
            }
        }

        _logger.LogInformation(
            "Seerr watchlist mirror for {Username}: +{Added} -{Removed} (add failures {AddFailed}, remove failures {RemoveFailed})",
            jellyfinUsername, added, removed, addFailed, removeFailed);
    }
}
