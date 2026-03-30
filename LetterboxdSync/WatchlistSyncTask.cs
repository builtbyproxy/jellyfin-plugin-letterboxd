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

            using var client = new LetterboxdClient(_logger);
            try
            {
                await client.AuthenticateAsync(account).ConfigureAwait(false);

                if (client.TokensRefreshed)
                {
                    Plugin.Instance!.SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Auth failed for {Username}: {Message}", user.Username, ex.Message);
                continue;
            }

            List<int> tmdbIds;
            try
            {
                tmdbIds = await client.GetWatchlistTmdbIdsAsync(account.LetterboxdUsername).ConfigureAwait(false);
                _logger.LogInformation("Found {Count} films in {Username}'s Letterboxd watchlist",
                    tmdbIds.Count, account.LetterboxdUsername);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to fetch watchlist for {Username}: {Message}", user.Username, ex.Message);
                continue;
            }

            if (tmdbIds.Count == 0)
                continue;

            // Find matching movies in Jellyfin library
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
            });

            var matchedItems = new List<Guid>();
            foreach (var tmdbId in tmdbIds)
            {
                var match = allMovies.FirstOrDefault(m =>
                    m.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());

                if (match != null)
                    matchedItems.Add(match.Id);
            }

            _logger.LogInformation("Matched {Matched}/{Total} watchlist films to Jellyfin library",
                matchedItems.Count, tmdbIds.Count);

            if (matchedItems.Count == 0)
                continue;

            // Find or create the playlist
            var playlistName = $"Letterboxd Watchlist";
            var existingPlaylists = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
            });

            var playlist = existingPlaylists.FirstOrDefault(p => p.Name == playlistName);

            if (playlist == null)
            {
                var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = playlistName,
                    UserId = user.Id,
                    MediaType = MediaType.Video,
                    ItemIdList = matchedItems.ToArray()
                }).ConfigureAwait(false);

                _logger.LogInformation("Created playlist '{Name}' with {Count} films for {Username}",
                    playlistName, matchedItems.Count, user.Username);
            }
            else
            {
                // Get existing playlist items
                var existingIds = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ParentId = playlist.Id,
                    Recursive = false,
                    OrderBy = new[] { (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) }
                }).Select(c => c.Id).ToHashSet();

                var newItems = matchedItems.Where(id => !existingIds.Contains(id)).ToArray();

                if (newItems.Length > 0)
                {
                    await _playlistManager.AddItemToPlaylistAsync(playlist.Id, newItems, user.Id)
                        .ConfigureAwait(false);

                    _logger.LogInformation("Added {Count} new films to playlist '{Name}' for {Username}",
                        newItems.Length, playlistName, user.Username);
                }
                else
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
