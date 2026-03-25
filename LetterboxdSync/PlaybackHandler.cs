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
            try
            {
                client.SetRawCookies(account.RawCookies);
                await client.AuthenticateAsync(account.LetterboxdUsername, account.LetterboxdPassword)
                    .ConfigureAwait(false);

                var film = await client.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);

                // Check diary for duplicates and rewatch detection
                var diary = await client.GetDiaryInfoAsync(film.Slug).ConfigureAwait(false);
                var viewingDate = DateTime.Now.Date;

                if (diary.LastDate != null && diary.LastDate.Value.Date == viewingDate)
                {
                    _logger.LogInformation("{Title} already logged on Letterboxd for {Date}, skipping",
                        e.Item.Name, viewingDate.ToString("yyyy-MM-dd"));
                    continue;
                }

                bool isRewatch = diary.HasAnyEntry;
                var userData = _userDataManager.GetUserData(user, e.Item!);
                bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);

                // Map Jellyfin rating (0-10) to Letterboxd (0.5-5.0 in 0.5 steps)
                double? lbRating = null;
                if (userData?.Rating.HasValue == true)
                {
                    var mapped = Math.Round(userData.Rating.Value / 2.0 * 2) / 2.0;
                    lbRating = Math.Clamp(mapped, 0.5, 5.0);
                }

                await client.MarkAsWatchedAsync(film.Slug, film.FilmId, DateTime.Now, liked,
                    film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

                var action = isRewatch ? "Logged rewatch of" : "Logged";
                _logger.LogInformation("{Action} {Title} to Letterboxd diary for {Username}",
                    action, e.Item.Name, account.LetterboxdUsername);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) to Letterboxd for {Username}: {Message}",
                    e.Item.Name, tmdbId, user.Username, ex.Message);
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
