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
                var existingItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ParentId = playlist.Id,
                    Recursive = false
                });
                var existingIds = existingItems.Select(c => c.Id).ToHashSet();

                // Add new items
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
                var removedItems = (tmdbIds.Count == 0 && existingIds.Count > 0)
                    ? Array.Empty<string>()
                    : existingItems
                        .Where(item => !watchlistItemIds.Contains(item.Id))
                        .Select(item => item.Id.ToString("N"))
                        .ToArray();

                if (removedItems.Length > 0)
                {
                    await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), removedItems)
                        .ConfigureAwait(false);
                    _logger.LogInformation("Removed {Count} films from playlist '{Name}' for {Username}",
                        removedItems.Length, playlistName, user.Username);
                }

                if (newItems.Length == 0 && removedItems.Length == 0)
                {
                    _logger.LogInformation("Playlist '{Name}' already up to date for {Username}",
                        playlistName, user.Username);
                }
            }
        }

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
