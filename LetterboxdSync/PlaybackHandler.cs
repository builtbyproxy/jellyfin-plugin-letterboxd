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

                // Check for duplicate
                var lastLog = await client.GetLastDiaryDateAsync(film.Slug).ConfigureAwait(false);
                var viewingDate = DateTime.Now.Date;

                if (lastLog != null && lastLog.Value.Date == viewingDate)
                {
                    _logger.LogInformation("{Title} already logged on Letterboxd for {Date}, skipping",
                        e.Item.Name, viewingDate.ToString("yyyy-MM-dd"));
                    continue;
                }

                var userData = _userDataManager.GetUserData(user, e.Item!);
                bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);

                await client.MarkAsWatchedAsync(film.Slug, film.FilmId, DateTime.Now, liked)
                    .ConfigureAwait(false);

                _logger.LogInformation("Logged {Title} to Letterboxd diary for {Username}",
                    e.Item.Name, account.LetterboxdUsername);
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
