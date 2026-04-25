using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class WatchlistSyncTask : IScheduledTask
{
    private readonly ILogger<WatchlistSyncTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPlaylistManager _playlistManager;

    public WatchlistSyncTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager)
    {
        _logger = loggerFactory.CreateLogger<WatchlistSyncTask>();
        _userManager = userManager;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public string Name => "Sync Letterboxd watchlist to playlist";
    public string Key => "LetterboxdWatchlistSync";
    public string Description => "Creates a Jellyfin playlist from your Letterboxd watchlist";
    public string Category => "Letterboxd";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = _userManager.Users.ToList();

        JellyseerrClient? jellyseerr = null;
        if (JellyseerrClient.IsConfigured(Config.JellyseerrUrl, Config.JellyseerrApiKey))
        {
            jellyseerr = new JellyseerrClient(Config.JellyseerrUrl!, Config.JellyseerrApiKey!, _logger);
        }

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = Config.Accounts.FirstOrDefault(
                a => a.Enabled && a.EnableWatchlistSync && a.UserJellyfinId == user.Id.ToString("N"));

            if (account == null)
                continue;

            _logger.LogInformation("Starting watchlist sync for {Username}", user.Username);

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
                continue;
            }

            using var _s = service;

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
                continue;
            }

            // Find matching movies in Jellyfin library
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });

            var watchlistItemIds = new HashSet<Guid>();
            foreach (var tmdbId in tmdbIds)
            {
                var match = allMovies.FirstOrDefault(m =>
                    m.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());

                if (match != null)
                    watchlistItemIds.Add(match.Id);
            }

            _logger.LogInformation("Matched {Matched}/{Total} watchlist films to Jellyfin library",
                watchlistItemIds.Count, tmdbIds.Count);

            // Find or create the playlist
            var playlistName = "Letterboxd Watchlist";
            var existingPlaylists = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
                Recursive = true
            });

            var playlist = existingPlaylists.FirstOrDefault(p => p.Name == playlistName);

            if (playlist == null)
            {
                if (watchlistItemIds.Count == 0)
                    continue;

                await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = playlistName,
                    UserId = user.Id,
                    MediaType = MediaType.Video,
                    ItemIdList = watchlistItemIds.ToArray()
                }).ConfigureAwait(false);

                _logger.LogInformation("Created playlist '{Name}' with {Count} films for {Username}",
                    playlistName, watchlistItemIds.Count, user.Username);
            }
            else
            {
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
                    await _playlistManager.AddItemToPlaylistAsync(playlist.Id, newItems, user.Id)
                        .ConfigureAwait(false);
                    _logger.LogInformation("Added {Count} new films to playlist '{Name}' for {Username}",
                        newItems.Length, playlistName, user.Username);
                }

                // Remove items no longer on watchlist.
                // Guard: if the scrape returned zero results but the playlist has items,
                // skip removal — an empty scrape result likely means Cloudflare blocked us,
                // not that the user cleared their entire watchlist.
                var removedIds = (tmdbIds.Count == 0 && existingIds.Count > 0)
                    ? Array.Empty<string>()
                    : existingIds
                        .Where(id => !watchlistItemIds.Contains(id))
                        .Select(id => id.ToString("N"))
                        .ToArray();

                if (removedIds.Length > 0)
                {
                    await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), removedIds)
                        .ConfigureAwait(false);
                    _logger.LogInformation("Removed {Count} films from playlist '{Name}' for {Username}",
                        removedIds.Length, playlistName, user.Username);
                }

                if (newItems.Length == 0 && removedIds.Length == 0)
                {
                    _logger.LogInformation("Playlist '{Name}' already up to date for {Username}",
                        playlistName, user.Username);
                }
            }

            // Auto-request unmatched watchlist films via Jellyseerr.
            if (account.AutoRequestWatchlist && jellyseerr != null)
            {
                var matchedTmdbIds = new HashSet<int>();
                foreach (var tmdbId in tmdbIds)
                {
                    var match = allMovies.FirstOrDefault(m =>
                        m.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());
                    if (match != null) matchedTmdbIds.Add(tmdbId);
                }
                var unmatchedTmdbIds = tmdbIds.Where(id => !matchedTmdbIds.Contains(id)).ToList();

                if (unmatchedTmdbIds.Count > 0)
                {
                    int? jellyseerrUserId;
                    try
                    {
                        jellyseerrUserId = await jellyseerr.GetJellyseerrUserIdAsync(account.UserJellyfinId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not fetch Jellyseerr user map for {Username}: {Message}",
                            user.Username, ex.Message);
                        jellyseerrUserId = null;
                    }

                    if (jellyseerrUserId == null)
                    {
                        _logger.LogWarning("No Jellyseerr user linked to Jellyfin user {Username}; skipping auto-request",
                            user.Username);
                    }
                    else
                    {
                        var requested = 0;
                        var failed = 0;
                        foreach (var tmdbId in unmatchedTmdbIds)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                if (await jellyseerr.RequestMovieAsync(tmdbId, jellyseerrUserId.Value).ConfigureAwait(false))
                                    requested++;
                                else
                                    failed++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Jellyseerr request errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message);
                                failed++;
                            }
                        }
                        _logger.LogInformation("Jellyseerr auto-request for {Username}: {Requested} requested, {Failed} failed of {Total} unmatched",
                            user.Username, requested, failed, unmatchedTmdbIds.Count);
                    }
                }
            }
        }

        jellyseerr?.Dispose();

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        }
    };
}
