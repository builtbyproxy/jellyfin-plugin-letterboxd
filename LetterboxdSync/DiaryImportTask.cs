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

            List<int> diaryTmdbIds;
            try
            {
                diaryTmdbIds = await client.GetDiaryTmdbIdsAsync(account.LetterboxdUsername).ConfigureAwait(false);
                _logger.LogInformation("Found {Count} films in {Username}'s Letterboxd diary",
                    diaryTmdbIds.Count, account.LetterboxdUsername);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to fetch diary for {Username}: {Message}", user.Username, ex.Message);
                continue;
            }

            if (diaryTmdbIds.Count == 0)
                continue;

            // Find unplayed movies in Jellyfin that are in the Letterboxd diary
            var unplayedMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                IsPlayed = false,
                Recursive = true
            });

            var marked = 0;
            foreach (var movie in unplayedMovies)
            {
                var tmdbStr = movie.GetProviderId(MetadataProvider.Tmdb);
                if (!int.TryParse(tmdbStr, out var tmdbId)) continue;

                if (!diaryTmdbIds.Contains(tmdbId)) continue;

                var userData = _userDataManager.GetUserData(user, movie);
                if (userData == null) continue;
                userData.Played = true;
                userData.LastPlayedDate = DateTime.UtcNow;
                _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.Import, cancellationToken);

                _logger.LogInformation("Marked {Title} as played for {Username} (from Letterboxd diary)",
                    movie.Name, user.Username);
                marked++;
            }

            _logger.LogInformation("Diary import complete for {Username}: {Marked} films marked as played",
                user.Username, marked);
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
