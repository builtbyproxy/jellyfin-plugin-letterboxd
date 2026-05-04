using System;
using System.Collections.Generic;
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

public class DiaryImportTask : IScheduledTask
{
    private readonly ILogger<DiaryImportTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public DiaryImportTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager)
    {
        _logger = loggerFactory.CreateLogger<DiaryImportTask>();
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public string Name => "Import Letterboxd diary to Jellyfin";
    public string Key => "LetterboxdDiaryImport";
    public string Description => "Marks films in your Jellyfin library as played if they appear in your Letterboxd diary";
    public string Category => "Letterboxd";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var users = _userManager.Users.ToList();

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = Config.Accounts.FirstOrDefault(
                a => a.Enabled && a.EnableDiaryImport && a.UserJellyfinId == user.Id.ToString("N"));

            if (account == null)
                continue;

            _logger.LogInformation("Starting diary import for {Username}", user.Username);
            SyncProgress.Start("Diary Import", "Authenticating");

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

            List<DiaryFilmEntry> diaryEntries;
            try
            {
                SyncProgress.SetPhase("Scanning Letterboxd films");
                diaryEntries = await service.GetDiaryFilmEntriesAsync(account.LetterboxdUsername).ConfigureAwait(false);
                _logger.LogInformation("Found {Count} films in {Username}'s Letterboxd diary",
                    diaryEntries.Count, account.LetterboxdUsername);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to fetch diary for {Username}: {Message}", user.Username, ex.Message);
                continue;
            }

            if (diaryEntries.Count == 0)
                continue;

            var ratingByTmdbId = diaryEntries
                .Where(e => e.Rating.HasValue)
                .ToDictionary(e => e.TmdbId, e => e.Rating!.Value);
            var diaryTmdbIds = new HashSet<int>(diaryEntries.Select(e => e.TmdbId));

            // Pull all movies (not just unplayed) so we can also apply rating-only updates
            // to films already marked as played.
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });

            var marked = 0;
            var ratingsApplied = 0;
            foreach (var movie in allMovies)
            {
                var tmdbStr = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!int.TryParse(tmdbStr, out var tmdbId)) continue;

                if (!diaryTmdbIds.Contains(tmdbId)) continue;

                var userData = _userDataManager.GetUserData(user, movie);
                if (userData == null) continue;

                var changed = false;

                // Mark as played if not already. Do NOT set LastPlayedDate, since that would
                // cause SyncTask to re-export this film back to Letterboxd (sync loop).
                if (!userData.Played)
                {
                    userData.Played = true;
                    changed = true;
                    marked++;
                    _logger.LogInformation("Marked {Title} as played for {Username} (from Letterboxd diary)",
                        movie.Name, user.Username);
                }

                // Apply Letterboxd rating only when Jellyfin doesn't already have one.
                // Jellyfin has no "rating last modified" timestamp to compare against, so we
                // use absence-of-rating as the safe signal. Existing ratings (e.g. from
                // Findroid) are preserved; users who want to overwrite from Letterboxd can
                // post a review via the plugin dashboard, which always wins.
                if (ratingByTmdbId.TryGetValue(tmdbId, out var lbRating) &&
                    (!userData.Rating.HasValue || userData.Rating.Value <= 0))
                {
                    var jfRating = Helpers.LetterboxdToJellyfinRating(lbRating);
                    if (jfRating.HasValue)
                    {
                        userData.Rating = jfRating;
                        changed = true;
                        ratingsApplied++;
                        _logger.LogInformation("Imported Letterboxd rating {LbRating} -> Jellyfin {JfRating} for {Title}",
                            lbRating, jfRating.Value, movie.Name);
                    }
                }

                if (changed)
                {
                    _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.Import, cancellationToken);
                }
            }

            // Count Letterboxd films/ratings we couldn't act on because the user doesn't
            // have them in their Jellyfin library yet. Useful for surfacing the gap so
            // users know their LB and JF aren't in full lockstep, and for support logs.
            var libraryTmdbIds = new HashSet<int>(allMovies
                .Select(m => m.GetProviderId(MetadataProvider.Tmdb))
                .Where(s => int.TryParse(s, out _))
                .Select(s => int.Parse(s!)));
            var unmatchedFilms = diaryTmdbIds.Count(id => !libraryTmdbIds.Contains(id));
            var unmatchedRatings = ratingByTmdbId.Count(kv => !libraryTmdbIds.Contains(kv.Key));

            _logger.LogInformation(
                "Diary import complete for {Username}: {Marked} marked played, {Ratings} ratings imported. " +
                "Skipped because not in Jellyfin library: {UnmatchedFilms} films ({UnmatchedRatings} of which were rated). " +
                "Skipped because Jellyfin rating already set: {RatingsHeldByJellyfin}.",
                user.Username, marked, ratingsApplied,
                unmatchedFilms, unmatchedRatings,
                ratingByTmdbId.Count(kv => libraryTmdbIds.Contains(kv.Key)) - ratingsApplied);
            SyncProgress.Complete();
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
