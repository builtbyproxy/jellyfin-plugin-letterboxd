using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class PlaybackHandler : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<PlaybackHandler> _logger;

    public PlaybackHandler(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILogger<PlaybackHandler> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await HandlePlaybackStoppedAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling playback stopped event");
        }
    }

    // internal (not private) so tests can drive the handler without going through
    // the ISessionManager event raise machinery; production callers still go via OnPlaybackStopped.
    internal async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
    {
        if (e.Item == null || !e.Item.IsMovie())
            return;

        if (!e.PlayedToCompletion)
            return;

        if (e.Users == null || e.Users.Count == 0)
            return;

        foreach (var user in e.Users)
        {
            var tmdbIdStr = e.Item.GetProviderId(MetadataProvider.Tmdb);
            if (!int.TryParse(tmdbIdStr, out var tmdbId))
            {
                _logger.LogWarning("Movie {Title} has no TMDb ID, skipping Letterboxd sync", e.Item.Name);
                continue;
            }

            // Fan out across all enabled Letterboxd accounts for this Jellyfin user.
            // Shared TV-user case (e.g. Lachlan + Deb both on the same Jellyfin login)
            // means each LB account independently gets the diary entry. One failing
            // account never blocks the others; rating mirroring is identical for both
            // since Jellyfin only stores one rating per (user, film).
            var accounts = Config.GetEnabledAccountsForUser(user.Id.ToString("N")).ToList();
            if (accounts.Count == 0)
                continue;

            foreach (var account in accounts)
            {
                _logger.LogInformation("Syncing {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser}",
                    e.Item.Name, tmdbId, user.Username, account.LetterboxdUsername);

                try
                {
                    using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                        account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                        .ConfigureAwait(false);

                    var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                    var viewingDate = DateTime.Now.Date;

                    var diaryInfo = await service.GetDiaryInfoAsync(film.FilmId, account.LetterboxdUsername).ConfigureAwait(false);

                    if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
                    {
                        _logger.LogInformation("{Title} already logged on Letterboxd ({LbUser}) for {Date}, skipping",
                            e.Item.Name, account.LetterboxdUsername, viewingDate.ToString("yyyy-MM-dd"));
                        SyncHistory.Record(new SyncEvent
                        {
                            FilmTitle = e.Item.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                            Username = user.Username, Timestamp = DateTime.UtcNow,
                            ViewingDate = viewingDate, Status = SyncStatus.Skipped, Source = "playback"
                        });
                        continue;
                    }

                    bool isRewatch = Helpers.IsRewatch(diaryInfo.LastDate, viewingDate);
                    var userData = _userDataManager.GetUserData(user, e.Item!);
                    bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);
                    double? lbRating = Helpers.MapRating(userData?.Rating);

                    await service.MarkAsWatchedAsync(film.Slug, film.FilmId, DateTime.Now, liked,
                        film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

                    var action = isRewatch ? "Logged rewatch of" : "Logged";
                    _logger.LogInformation("{Action} {Title} to Letterboxd diary for {Username} as {LbUser}",
                        action, e.Item.Name, user.Username, account.LetterboxdUsername);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                        Username = user.Username, Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate,
                        Status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success,
                        Source = "playback"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser}: {Message}",
                        e.Item.Name, tmdbId, user.Username, account.LetterboxdUsername, ex.Message);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name, TmdbId = tmdbId,
                        Username = user.Username, Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed, Error = ex.Message, Source = "playback"
                    });
                }
            }
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }
}

internal static class ItemExtensions
{
    public static bool IsMovie(this MediaBrowser.Controller.Entities.BaseItem item)
        => item.GetBaseItemKind() == BaseItemKind.Movie;
}
