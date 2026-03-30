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

    private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
    {
        if (e.Item == null || !e.Item.IsMovie())
            return;

        if (!e.PlayedToCompletion)
            return;

        if (e.Users == null || e.Users.Count == 0)
            return;

        foreach (var user in e.Users)
        {
            var account = Config.Accounts.FirstOrDefault(
                a => a.Enabled && a.UserJellyfinId == user.Id.ToString("N"));

            if (account == null)
                continue;

            var tmdbIdStr = e.Item.GetProviderId(MetadataProvider.Tmdb);
            if (!int.TryParse(tmdbIdStr, out var tmdbId))
            {
                _logger.LogWarning("Movie {Title} has no TMDb ID, skipping Letterboxd sync", e.Item.Name);
                continue;
            }

            _logger.LogInformation("Syncing {Title} (TMDb:{TmdbId}) to Letterboxd for {Username}",
                e.Item.Name, tmdbId, user.Username);

            using var client = new LetterboxdClient(_logger);
            FilmResult? film = null;
            try
            {
                await client.AuthenticateAsync(account).ConfigureAwait(false);

                if (client.TokensRefreshed)
                {
                    Plugin.Instance!.SaveConfiguration();
                }

                film = await client.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);

                // Check diary for duplicates and rewatch detection
                var diary = await client.GetDiaryInfoAsync(film.FilmId).ConfigureAwait(false);
                var viewingDate = DateTime.Now.Date;

                if (diary.LastDate != null && diary.LastDate.Value.Date == viewingDate)
                {
                    _logger.LogInformation("{Title} ({FilmId}) already logged or watched on Letterboxd today, skipping duplicate sync", e.Item.Name, film.FilmId);

                    if (account.SyncFavorites)
                    {
                        try
                        {
                            var ud = _userDataManager.GetUserData(user, e.Item!);
                            await client.SetFilmLikeAsync(film.FilmId, ud?.IsFavorite ?? false).ConfigureAwait(false);
                        }
                        catch (Exception likeEx)
                        {
                            _logger.LogWarning("Failed to sync like status for skipped film {Title} ({FilmId}): {Message}", e.Item.Name, film.FilmId, likeEx.Message);
                        }
                    }

                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name, FilmSlug = film.Slug, TmdbId = tmdbId,
                        Username = user.Username, Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate, Status = SyncStatus.Skipped, Source = "playback"
                    });
                    continue;
                }

                bool isRewatch = diary.IsWatched || diary.HasAnyEntry;
                var userData = _userDataManager.GetUserData(user, e.Item!);
                bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);

                double? lbRating = null;
                if (userData?.Rating.HasValue == true)
                {
                    var mapped = Math.Round(userData.Rating.Value / 2.0 * 2) / 2.0;
                    lbRating = Math.Clamp(mapped, 0.5, 5.0);
                }

                await client.MarkAsWatchedAsync(film.Slug, film.FilmId, DateTime.Now, liked,
                    film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

                if (account.SyncFavorites)
                {
                    try
                    {
                        await client.SetFilmLikeAsync(film.FilmId, userData?.IsFavorite ?? false).ConfigureAwait(false);
                    }
                    catch (Exception likeEx)
                    {
                        _logger.LogWarning("Failed to sync like status for {Title} ({FilmId}): {Message}", e.Item.Name, film.FilmId, likeEx.Message);
                    }
                }

                var action = isRewatch ? "Logged rewatch of" : "Logged";
                _logger.LogInformation("{Action} {Title} to Letterboxd diary for {Username}",
                    action, e.Item.Name, account.LetterboxdUsername);
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
                if (film != null)
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}, FilmId:{FilmId}) to Letterboxd for {Username}: {Message}",
                        e.Item.Name, tmdbId, film.FilmId, user.Username, ex.Message);
                }
                else
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) to Letterboxd for {Username}: {Message}",
                        e.Item.Name, tmdbId, user.Username, ex.Message);
                }

                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = e.Item.Name, TmdbId = tmdbId,
                    Username = user.Username, Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed, Error = ex.Message, Source = "playback"
                });
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
