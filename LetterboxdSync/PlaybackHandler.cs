using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Entities.TV;
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
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    // activityManager is optional so existing construction sites (and tests) keep
    // working; a null only skips the auth-breaker admin notification.
    public PlaybackHandler(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILogger<PlaybackHandler> logger,
        MediaBrowser.Model.Activity.IActivityManager? activityManager = null)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _logger = logger;
        _activityManager = activityManager;
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
        if (e.Item == null)
            return;

        if (!e.PlayedToCompletion)
            return;

        if (e.Users == null || e.Users.Count == 0)
            return;

        // TV episodes scrobble to Serializd; films fall through to the Letterboxd path below.
        // The two are fully isolated: a Serializd failure never touches Letterboxd and vice versa.
        if (e.Item is Episode episode)
        {
            await HandleEpisodeAsync(episode, e).ConfigureAwait(false);
            return;
        }

        if (!e.Item.IsMovie())
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
                var breakerUserId = user.Id.ToString("N");
                if (AuthBreaker.IsOpen(breakerUserId, account.LetterboxdUsername))
                {
                    _logger.LogInformation(
                        "Skipping real-time sync of {Title} for {LbUser}: auth breaker open; the scheduled task catches up once credentials are re-saved",
                        e.Item.Name, account.LetterboxdUsername);
                    continue;
                }

                _logger.LogInformation("Syncing {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser}",
                    e.Item.Name, tmdbId, user.Username, account.LetterboxdUsername);

                // Authentication gets its own try/catch so only genuine login failures
                // feed the breaker; errors later in the sync (lookups, Cloudflare) fall
                // through to the existing catch below and never count against auth.
                ILetterboxdService authedService;
                try
                {
                    authedService = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                        account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Auth failed for {Username} as {LbUser}: {Message}",
                        user.Username, account.LetterboxdUsername, ex.Message);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Error = ex.Message,
                        Source = "playback"
                    });
                    if (AuthBreaker.RecordFailure(breakerUserId, account.LetterboxdUsername, ex.Message))
                        await AuthBreaker.NotifyOpenedAsync(_activityManager, user.Id, account.LetterboxdUsername, _logger).ConfigureAwait(false);
                    continue;
                }

                AuthBreaker.RecordSuccess(breakerUserId, account.LetterboxdUsername);

                try
                {
                    using var service = authedService;

                    var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                    var viewingDate = DateTime.Now.Date;

                    var diaryInfo = await service.GetDiaryInfoAsync(film.FilmId, account.LetterboxdUsername).ConfigureAwait(false);

                    if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
                    {
                        _logger.LogInformation("{Title} already logged on Letterboxd ({LbUser}) for {Date}, skipping",
                            e.Item.Name, account.LetterboxdUsername, viewingDate.ToString("yyyy-MM-dd"));
                        SyncHistory.Record(new SyncEvent
                        {
                            FilmTitle = e.Item.Name,
                            FilmSlug = film.Slug,
                            TmdbId = tmdbId,
                            Username = user.Username,
                            Timestamp = DateTime.UtcNow,
                            ViewingDate = viewingDate,
                            Status = SyncStatus.Skipped,
                            Source = "playback"
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
                        FilmTitle = e.Item.Name,
                        FilmSlug = film.Slug,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
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
                        FilmTitle = e.Item.Name,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Error = ex.Message,
                        Source = "playback"
                    });
                }
            }
        }
    }

    // internal so tests can drive the episode path directly, same as HandlePlaybackStoppedAsync.
    internal async Task HandleEpisodeAsync(Episode episode, PlaybackStopEventArgs e)
    {
        var seriesTmdbId = SeriesTmdbIdReader(episode);
        var epRef = SerializdEpisodeMapper.Build(
            seriesTmdbId, episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd);

        if (epRef == null)
        {
            _logger.LogWarning(
                "Episode {Series} S{Season}E{Episode} lacks a series TMDb id / season / episode number, skipping Serializd sync",
                episode.SeriesName ?? episode.Name, episode.ParentIndexNumber, episode.IndexNumber);
            return;
        }

        foreach (var user in e.Users)
        {
            var accounts = Config.GetEnabledSerializdAccountsForUser(user.Id.ToString("N")).ToList();
            if (accounts.Count == 0)
                continue;

            foreach (var account in accounts)
            {
                try
                {
                    using var service = await SerializdServiceFactory
                        .CreateAuthenticatedAsync(account.Email, account.Password, _logger)
                        .ConfigureAwait(false);

                    var seasonId = await service
                        .ResolveSeasonIdAsync(epRef.ShowTmdbId, epRef.SeasonNumber)
                        .ConfigureAwait(false);

                    if (seasonId == null)
                    {
                        _logger.LogWarning(
                            "Serializd has no season {Season} for TMDb show {TmdbId} ({Series}), skipping",
                            epRef.SeasonNumber, epRef.ShowTmdbId, episode.SeriesName);
                        continue;
                    }

                    var userId = user.Id.ToString("N");

                    // 1. Mark the episodes watched (populates Shows/Stats).
                    await service.LogEpisodesAsync(epRef.ShowTmdbId, seasonId.Value, epRef.EpisodeNumbers)
                        .ConfigureAwait(false);
                    foreach (var n in epRef.EpisodeNumbers)
                        SerializdSyncHistory.Record(userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n);

                    // 2. Create a dated Diary log per episode, stamped now (the watch just
                    // finished), carrying the episode's Jellyfin rating if it has one. A second
                    // finish of the same episode is logged as a rewatch.
                    var rating = SerializdRating.FromJellyfin(_userDataManager.GetUserData(user, episode)?.Rating);
                    foreach (var n in epRef.EpisodeNumbers)
                    {
                        var isRewatch = SerializdSyncHistory.Has(
                            userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n, SerializdSyncHistory.KindLog);
                        await service.CreateEpisodeLogAsync(
                            epRef.ShowTmdbId, seasonId.Value, n, DateTime.UtcNow, rating, isRewatch)
                            .ConfigureAwait(false);
                        SerializdSyncHistory.Record(
                            userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n, SerializdSyncHistory.KindLog);

                        SerializdActivity.Record(new SyncEvent
                        {
                            FilmTitle = $"{episode.SeriesName} · S{epRef.SeasonNumber}E{n}",
                            TmdbId = epRef.ShowTmdbId,
                            Username = user.Username ?? string.Empty,
                            Timestamp = DateTime.UtcNow,
                            // UTC, matching SerializdSyncRunner's WatchedAtUtc: a local-date stamp
                            // here would show a different calendar day near midnight than the
                            // catch-up path records for the same logical watch.
                            ViewingDate = DateTime.UtcNow.Date,
                            Status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success,
                            Source = "playback",
                        });
                    }

                    _logger.LogInformation(
                        "Logged {Series} S{Season} episodes {Episodes} (TMDb:{TmdbId}) to Serializd for {Username} as {Email}",
                        episode.SeriesName, epRef.SeasonNumber, string.Join(",", epRef.EpisodeNumbers),
                        epRef.ShowTmdbId, user.Username, account.Email);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Failed to log {Series} S{Season}E{Episode} (TMDb:{TmdbId}) to Serializd for {Username}: {Message}",
                        episode.SeriesName, epRef.SeasonNumber, episode.IndexNumber, epRef.ShowTmdbId,
                        user.Username, ex.Message);
                    SerializdActivity.Record(new SyncEvent
                    {
                        FilmTitle = episode.SeriesName ?? episode.Name ?? "Episode",
                        TmdbId = epRef.ShowTmdbId,
                        Username = user.Username ?? string.Empty,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Error = ex.Message,
                        Source = "playback",
                    });
                }
            }
        }
    }

    // Reads the parent series' TMDb id from an episode. Overridable so tests can supply
    // it without wiring Jellyfin's library-parent graph (mirrors the factory OverrideForTesting
    // convention); production delegates to the shared reader on SerializdSyncRunner so the
    // real-time and catch-up paths use identical logic.
    internal static Func<Episode, int?> SeriesTmdbIdReader { get; set; } =
        Serializd.SerializdSyncRunner.ReadSeriesTmdbId;

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
