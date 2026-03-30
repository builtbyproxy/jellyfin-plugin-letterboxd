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
            SyncProgress.Start("Letterboxd Sync", "Authenticating");

            List<MediaBrowser.Controller.Entities.BaseItem> movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                IsPlayed = true,
            }).ToList();

            if (movies.Count == 0)
                continue;

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

            using var httpClient = new LetterboxdHttpClient(_logger);
            var auth = new LetterboxdAuth(httpClient, _logger);
            var scraper = new LetterboxdScraper(httpClient, _logger);
            var diary = new LetterboxdDiary(httpClient, auth, scraper, _logger);

            try
            {
                httpClient.SetRawCookies(account.RawCookies);
                await auth.AuthenticateAsync(account.LetterboxdUsername, account.LetterboxdPassword)
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

            SyncProgress.SetPhase("Syncing films");
            SyncProgress.SetTotal(movies.Count);

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
                    var film = await scraper.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                    await Task.Delay(3000 + Random.Shared.Next(2000), cancellationToken).ConfigureAwait(false);

                    var userData = _userDataManager.GetUserData(user, movie);
                    var viewingDate = userData?.LastPlayedDate?.Date ?? DateTime.Now.Date;

                    var diaryInfo = await scraper.GetDiaryInfoAsync(film.Slug, account.LetterboxdUsername).ConfigureAwait(false);
                    if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
                    {
                        _logger.LogDebug("{Title} already logged on {Date}, skipping",
                            movie.Name, viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        SyncHistory.Record(new SyncEvent
                        {
                            FilmTitle = movie.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                            Username = user.Username, Timestamp = DateTime.UtcNow,
                            ViewingDate = viewingDate, Status = SyncStatus.Skipped, Source = "scheduled"
                        });
                        skipped++;
                        continue;
                    }

                    // Scheduled sync never marks as rewatch
                    bool isRewatch = false;
                    bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);
                    double? lbRating = Helpers.MapRating(userData?.Rating);

                    await diary.MarkAsWatchedAsync(film.Slug, film.FilmId, userData?.LastPlayedDate, liked,
                        film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

                    _logger.LogInformation("Logged {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} on {Date}",
                        movie.Name, tmdbId, user.Username,
                        viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                        Username = user.Username, Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate, Status = SyncStatus.Success, Source = "scheduled"
                    });
                    synced++;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) for {Username}: {Message}",
                        movie.Name, tmdbId, user.Username, ex.Message);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name, TmdbId = tmdbId,
                        Username = user.Username, Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed, Error = ex.Message, Source = "scheduled"
                    });
                    failed++;
                }
            }

            _logger.LogInformation("Letterboxd sync complete for {Username}: {Synced} synced, {Skipped} skipped, {Failed} failed",
                user.Username, synced, skipped, failed);
            SyncProgress.Complete();

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
