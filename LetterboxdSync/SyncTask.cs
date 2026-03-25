using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class SyncTask : IScheduledTask
{
    private readonly ILogger<SyncTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public SyncTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager)
    {
        _logger = loggerFactory.CreateLogger<SyncTask>();
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public string Name => "Sync watched movies to Letterboxd";
    public string Key => "LetterboxdSync";
    public string Description => "Syncs your Jellyfin watch history to your Letterboxd diary";
    public string Category => "Letterboxd";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = _userManager.Users.ToList();
        var processedUsers = 0;

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = Config.Accounts.FirstOrDefault(
                a => a.Enabled && a.UserJellyfinId == user.Id.ToString("N"));

            if (account == null)
                continue;

            _logger.LogInformation("Starting Letterboxd sync for {Username}", user.Username);

            List<MediaBrowser.Controller.Entities.BaseItem> movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                IsPlayed = true,
            }).ToList();

            if (movies.Count == 0)
                continue;

            // Apply date filter if enabled
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
                continue;

            using var client = new LetterboxdClient(_logger);
            try
            {
                client.SetRawCookies(account.RawCookies);
                await client.AuthenticateAsync(account.LetterboxdUsername, account.LetterboxdPassword)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Auth failed for {Username}: {Message}", user.Username, ex.Message);
                continue;
            }

            var synced = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tmdbIdStr = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!int.TryParse(tmdbIdStr, out var tmdbId))
                {
                    _logger.LogWarning("{Title} has no TMDb ID, skipping", movie.Name);
                    skipped++;
                    continue;
                }

                try
                {
                    var film = await client.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                    await Task.Delay(1000 + Random.Shared.Next(1000), cancellationToken).ConfigureAwait(false);

                    var userData = _userDataManager.GetUserData(user, movie);
                    var viewingDate = userData?.LastPlayedDate?.Date ?? DateTime.Now.Date;

                    // Duplicate check
                    var lastLog = await client.GetLastDiaryDateAsync(film.Slug).ConfigureAwait(false);
                    if (lastLog != null && lastLog.Value.Date == viewingDate)
                    {
                        _logger.LogDebug("{Title} already logged on {Date}, skipping",
                            movie.Name, viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        skipped++;
                        continue;
                    }

                    bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);

                    await client.MarkAsWatchedAsync(film.Slug, film.FilmId, userData?.LastPlayedDate, liked)
                        .ConfigureAwait(false);

                    _logger.LogInformation("Logged {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} on {Date}",
                        movie.Name, tmdbId, user.Username,
                        viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    synced++;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) for {Username}: {Message}",
                        movie.Name, tmdbId, user.Username, ex.Message);
                    failed++;
                }
            }

            _logger.LogInformation("Letterboxd sync complete for {Username}: {Synced} synced, {Skipped} skipped, {Failed} failed",
                user.Username, synced, skipped, failed);

            processedUsers++;
            progress.Report((double)processedUsers / users.Count * 100);
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
