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
/// Performs the Letterboxd-watchlist → Jellyfin-playlist → Jellyseerr chain. Used by both
/// the scheduled <see cref="WatchlistSyncTask"/> and the user-triggered API endpoint, gated
/// behind <see cref="SyncGate"/> so it serialises with the diary sync.
/// </summary>
public class WatchlistSyncRunner
{
    private readonly ILogger<WatchlistSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPlaylistManager _playlistManager;

    public WatchlistSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPlaylistManager playlistManager)
    {
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

            var users = _userManager.Users.ToList();
            var processed = 0;
            SyncProgress.Start("Letterboxd Watchlist", "Starting");

            foreach (var user in users)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var account = Config.Accounts.FirstOrDefault(
                    a => a.Enabled && a.EnableWatchlistSync && a.UserJellyfinId == user.Id.ToString("N"));
                if (account == null) continue;

                await SyncOneUserAsync(user, account, jellyseerr, source, cancellationToken).ConfigureAwait(false);

                processed++;
                progress.Report((double)processed / users.Count * 100);
            }

            progress.Report(100);
            SyncProgress.Complete();
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    /// <summary>
    /// Run watchlist sync for a single user. Returns false if another sync is already
    /// running, the user is unknown, or they have no enabled-with-watchlist account.
    /// </summary>
    public async Task<bool> TryRunForUserAsync(string userJellyfinId, string source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Another sync is already running, refusing user-triggered watchlist start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            var user = _userManager.Users.FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found, cannot start watchlist sync", userJellyfinId);
                return false;
            }

            var account = Config.Accounts.FirstOrDefault(
                a => a.Enabled && a.EnableWatchlistSync && a.UserJellyfinId == userJellyfinId);
            if (account == null)
            {
                _logger.LogWarning("No enabled watchlist-sync account for {Username}", user.Username);
                return false;
            }

            using var jellyseerr = CreateJellyseerrClient();
            SyncProgress.Start("Letterboxd Watchlist", "Starting");
            await SyncOneUserAsync(user, account, jellyseerr, source, cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            SyncProgress.Complete();
            return true;
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    private JellyseerrClient? CreateJellyseerrClient()
        => JellyseerrClient.IsConfigured(Config.JellyseerrUrl, Config.JellyseerrApiKey)
            ? new JellyseerrClient(Config.JellyseerrUrl!, Config.JellyseerrApiKey!, _logger)
            : null;

    private async Task SyncOneUserAsync(User user, Account account, JellyseerrClient? jellyseerr, string source, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting watchlist sync for {Username} (source={Source})", user.Username, source);
        SyncProgress.SetPhase($"Authenticating {user.Username}");

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
            return;
        }

        using var _s = service;

        SyncProgress.SetPhase($"Fetching watchlist for {user.Username}");
        List<int> tmdbIds;
        try
        {
            tmdbIds = await service.GetWatchlistTmdbIdsAsync(account.LetterboxdUsername).ConfigureAwait(false);
            _logger.LogInformation("Found {Count} films in {Username}'s Letterboxd watchlist",
                tmdbIds.Count, account.LetterboxdUsername);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to fetch watchlist for {Username}: {Message}", user.Username, ex.Message);
            return;
        }

        SyncProgress.SetPhase($"Updating Jellyfin playlist for {user.Username}");
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

        await UpdatePlaylistAsync(user, watchlistItemIds, tmdbIds.Count).ConfigureAwait(false);

        // Jellyseerr integration: auto-request unmatched films and/or mirror the
        // Letterboxd watchlist into the user's Jellyseerr watchlist.
        var jellyseerrWanted = jellyseerr != null && (account.AutoRequestWatchlist || account.MirrorJellyseerrWatchlist);
        if (!jellyseerrWanted) return;

        int? jellyseerrUserId;
        try
        {
            jellyseerrUserId = await jellyseerr!.GetJellyseerrUserIdAsync(account.UserJellyfinId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not fetch Jellyseerr user map for {Username}: {Message}",
                user.Username, ex.Message);
            return;
        }

        if (jellyseerrUserId == null)
        {
            _logger.LogWarning("No Jellyseerr user linked to Jellyfin user {Username}; skipping Jellyseerr sync",
                user.Username);
            return;
        }

        if (account.MirrorJellyseerrWatchlist)
        {
            SyncProgress.SetPhase($"Mirroring Jellyseerr watchlist for {user.Username}");
            await MirrorJellyseerrWatchlistAsync(jellyseerr!, jellyseerrUserId.Value, tmdbIds, user.Username!, cancellationToken)
                .ConfigureAwait(false);
        }

        if (account.AutoRequestWatchlist)
        {
            SyncProgress.SetPhase($"Requesting missing films via Jellyseerr for {user.Username}");
            var unmatchedTmdbIds = tmdbIds.Where(id => !matchedTmdbIds.Contains(id)).ToList();
            if (unmatchedTmdbIds.Count == 0) return;

            var requested = 0;
            var alreadyExists = 0;
            var failed = 0;
            foreach (var tmdbId in unmatchedTmdbIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await jellyseerr!.RequestMovieAsync(tmdbId, jellyseerrUserId.Value).ConfigureAwait(false);
                    switch (result)
                    {
                        case JellyseerrClient.RequestResult.Requested: requested++; break;
                        case JellyseerrClient.RequestResult.AlreadyExists: alreadyExists++; break;
                        default: failed++; break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Jellyseerr request errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                    failed++;
                }
            }
            _logger.LogInformation(
                "Jellyseerr auto-request for {Username}: {Requested} new, {Existing} already on Jellyseerr, {Failed} failed of {Total} unmatched",
                user.Username, requested, alreadyExists, failed, unmatchedTmdbIds.Count);
        }
    }

    private async Task UpdatePlaylistAsync(User user, HashSet<Guid> watchlistItemIds, int letterboxdCount)
    {
        const string playlistName = "Letterboxd Watchlist";

        var existingPlaylists = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true
        });

        var playlist = existingPlaylists.FirstOrDefault(p => p.Name == playlistName);

        if (playlist == null)
        {
            if (watchlistItemIds.Count == 0) return;

            await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = watchlistItemIds.ToArray()
            }).ConfigureAwait(false);

            _logger.LogInformation("Created playlist '{Name}' with {Count} films for {Username}",
                playlistName, watchlistItemIds.Count, user.Username);
            return;
        }

        // Source of truth: Playlist.LinkedChildren contains the wrapped media item IDs
        // (the underlying Movie GUIDs). Querying ParentId returns playlist *entries* whose
        // BaseItem.Id is the entry GUID, not the movie GUID — using that for dedup compared
        // entry IDs against movie IDs and never matched, causing duplicates each run.
        var playlistObj = (Playlist)playlist;
        var existingIds = playlistObj.LinkedChildren
            .Where(lc => lc.ItemId.HasValue)
            .Select(lc => lc.ItemId!.Value)
            .ToHashSet();

        var newItems = watchlistItemIds.Where(id => !existingIds.Contains(id)).ToArray();
        if (newItems.Length > 0)
        {
            await _playlistManager.AddItemToPlaylistAsync(playlist.Id, newItems, user.Id).ConfigureAwait(false);
            _logger.LogInformation("Added {Count} new films to playlist '{Name}' for {Username}",
                newItems.Length, playlistName, user.Username);
        }

        // Empty Letterboxd scrape probably means Cloudflare blocked us, not that the
        // user wiped their watchlist; skip removal so we don't gut the playlist.
        var removedIds = (letterboxdCount == 0 && existingIds.Count > 0)
            ? Array.Empty<string>()
            : existingIds
                .Where(id => !watchlistItemIds.Contains(id))
                .Select(id => id.ToString("N"))
                .ToArray();

        if (removedIds.Length > 0)
        {
            await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), removedIds).ConfigureAwait(false);
            _logger.LogInformation("Removed {Count} films from playlist '{Name}' for {Username}",
                removedIds.Length, playlistName, user.Username);
        }

        if (newItems.Length == 0 && removedIds.Length == 0)
        {
            _logger.LogInformation("Playlist '{Name}' already up to date for {Username}",
                playlistName, user.Username);
        }
    }

    private async Task MirrorJellyseerrWatchlistAsync(
        JellyseerrClient jellyseerr,
        int jellyseerrUserId,
        List<int> letterboxdTmdbIds,
        string jellyfinUsername,
        CancellationToken cancellationToken)
    {
        if (letterboxdTmdbIds.Count == 0)
        {
            _logger.LogWarning(
                "Empty Letterboxd watchlist for {Username}; skipping Jellyseerr mirror to avoid mass-deletion",
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
            _logger.LogWarning("Failed to fetch Jellyseerr watchlist for {Username}: {Message}",
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
                _logger.LogWarning("Jellyseerr watchlist add errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
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
                _logger.LogWarning("Jellyseerr watchlist remove errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                removeFailed++;
            }
        }

        _logger.LogInformation(
            "Jellyseerr watchlist mirror for {Username}: +{Added} -{Removed} (add failures {AddFailed}, remove failures {RemoveFailed})",
            jellyfinUsername, added, removed, addFailed, removeFailed);
    }
}
