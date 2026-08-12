using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Scheduled catch-up: logs Jellyfin-watched TV episodes to Serializd for every enabled
/// Serializd account, skipping episodes already logged (real-time or a prior run). The TV
/// counterpart to <see cref="LetterboxdSyncRunner"/>, but simpler: no rewatch logic, no
/// Cloudflare, and dedup is a local key set rather than a round trip.
///
/// Uses its own gate (<see cref="SerializdSyncGate"/>) so a Serializd run never
/// false-serialises against a Letterboxd run, the two hit different origins.
/// </summary>
public class SerializdSyncRunner
{
    private readonly ILogger<SerializdSyncRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public SerializdSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SerializdSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Reads the parent series' TMDb id from an episode. Overridable so the runner's tests
    /// can supply ids without wiring Jellyfin's library-parent graph. Production reads the
    /// resolved Series entity (populated at runtime).
    /// </summary>
    internal static Func<Episode, int?> SeriesTmdbIdReader { get; set; } = ReadSeriesTmdbId;

    /// <summary>
    /// The one place that knows where a show's TMDb id lives: on the parent Series
    /// entity, not the episode's own ProviderIds (those carry the episode-level id,
    /// which is wrong for Serializd). PlaybackHandler delegates here too, so the
    /// real-time and catch-up paths cannot diverge.
    /// </summary>
    internal static int? ReadSeriesTmdbId(Episode episode)
    {
        var s = episode.Series?.GetProviderId(MetadataProvider.Tmdb);
        return int.TryParse(s, out var id) ? id : (int?)null;
    }

    public async Task RunForAllAsync(IProgress<double> progress, string source, CancellationToken cancellationToken)
    {
        if (!await SerializdSyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd sync already running, skipping {Source} run", source);
            return;
        }

        try
        {
            SyncProgress.Start(SyncProgress.TrackSerializd, "Serializd TV sync", "Starting");
            var pairs = _userManager.GetUsers()
                .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
                    .Select(a => (User: u, Account: a)))
                .ToList();

            var processed = 0;
            foreach (var (user, account) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SyncOneAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Serializd catch-up failed for {Username} as {Email}: {Message}",
                        user.Username, account.Email, ex.Message);
                    // No SyncEvent is recorded on this early-exit path (e.g. an auth failure
                    // before SyncOneAsync reaches any episode); hook telemetry directly.
                    TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
                }

                processed++;
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
        }
        finally
        {
            SyncProgress.Complete(SyncProgress.TrackSerializd);
            SerializdSyncGate.Instance.Release();
        }
    }

    /// <summary>Runs the catch-up for a single Jellyfin user across all their enabled Serializd accounts.</summary>
    public async Task<bool> TryRunForUserAsync(string userJellyfinId, string source, CancellationToken cancellationToken)
    {
        if (!await SerializdSyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd sync already running, refusing user-triggered start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            SyncProgress.Start(SyncProgress.TrackSerializd, "Serializd TV sync", "Starting");
            var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null) return false;

            var accounts = Config.GetEnabledSerializdAccountsForUser(userJellyfinId).ToList();
            if (accounts.Count == 0) return false;

            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SyncOneAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Serializd catch-up failed for {Username} as {Email}: {Message}",
                        user.Username, account.Email, ex.Message);
                    // No SyncEvent is recorded on this early-exit path (e.g. an auth failure
                    // before SyncOneAsync reaches any episode); hook telemetry directly.
                    TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
                }
            }

            return true;
        }
        finally
        {
            SyncProgress.Complete(SyncProgress.TrackSerializd);
            SerializdSyncGate.Instance.Release();
        }
    }

    private async Task SyncOneAsync(User user, SerializdAccount account, string source, CancellationToken cancellationToken)
    {
        var userId = user.Id.ToString("N");

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            IsPlayed = true,
        });

        // Map played episodes to per-episode records carrying the real watch date + rating,
        // dropping anything we can't key (no series TMDb id / season / episode number).
        var records = new List<EpisodePlay>();
        var seriesById = new Dictionary<int, Series>();
        var skippedNoPlayDate = 0;
        foreach (var item in episodes)
        {
            if (item is not Episode ep) continue;
            var epRef = SerializdEpisodeMapper.Build(
                SeriesTmdbIdReader(ep), ep.ParentIndexNumber, ep.IndexNumber, ep.IndexNumberEnd);
            if (epRef == null) continue;

            var ud = _userDataManager.GetUserData(user, ep);

            // Skip episodes marked played on Jellyfin with no plausible LastPlayedDate: either
            // missing entirely, or an epoch-adjacent value (e.g. 1970-01-01) some clients send
            // when marking an item watched manually without a real timestamp (issue #106).
            // There's no real watch date to log: watchedAt would otherwise fall back to
            // DateTime.UtcNow (today) or literally 1970, which drifts forward on every run and
            // re-logs the same episode every catch-up. This is also the import-then-export loop
            // guard: SerializdDiaryImportRunner marks episodes played without a LastPlayedDate
            // specifically so they land here.
            if (ud is null || !Helpers.HasPlausibleWatchDate(ud.LastPlayedDate))
            {
                skippedNoPlayDate++;
                continue;
            }

            var watchedAt = ud.LastPlayedDate!.Value.ToUniversalTime();
            var rating = SerializdRating.FromJellyfin(ud.Rating);
            foreach (var n in epRef.EpisodeNumbers)
                records.Add(new EpisodePlay(epRef.ShowTmdbId, epRef.SeasonNumber, n, watchedAt, rating, ep.SeriesName ?? string.Empty));

            // The parent Series is already resolved here; cache it so SyncShowMetaAsync doesn't
            // need a second full-library scan to re-find the same shows.
            if (ep.Series is Series series && !seriesById.ContainsKey(epRef.ShowTmdbId))
                seriesById[epRef.ShowTmdbId] = series;
        }

        if (skippedNoPlayDate > 0)
            _logger.LogInformation(
                "Serializd catch-up: skipping {Count} episodes for {Username}: marked played but no plausible LastPlayedDate (no real watch date to log)",
                skippedNoPlayDate, user.Username);

        // Date filter: limit the catch-up to episodes watched within the look-back window.
        if (account.EnableDateFilter)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, account.DateFilterDays));
            records = records.Where(r => r.WatchedAtUtc >= cutoff).ToList();
        }

        // "Skip previously synced" (default on) short-circuits via the local dedup history.
        // When off, everything is re-sent (which re-logs, i.e. can create duplicate diary rows).
        bool AlreadyWatched(int s, int se, int e) =>
            account.SkipPreviouslySynced && SerializdSyncHistory.Has(userId, account.Email, s, se, e, SerializdSyncHistory.KindWatched);
        bool AlreadyLogged(EpisodePlay r) =>
            account.SkipPreviouslySynced && SerializdSyncHistory.Has(userId, account.Email, r.Show, r.Season, r.Episode, SerializdSyncHistory.KindLog);

        var needsWatched = GroupNewEpisodes(records.Select(r => (r.Show, r.Season, r.Episode)), AlreadyWatched);

        // Dedup within this batch (same as GroupNewEpisodes above): duplicate/unmerged Episode
        // items for the same (show, season, episode) must only produce one dated diary log.
        var seenLog = new HashSet<(int Show, int Season, int Episode)>();
        var needsLog = records
            .Where(r => !AlreadyLogged(r) && seenLog.Add((r.Show, r.Season, r.Episode)))
            .ToList();

        if (needsWatched.Count == 0 && needsLog.Count == 0)
        {
            _logger.LogDebug("Serializd catch-up: nothing new for {Username} as {Email}", user.Username, account.Email);
            return;
        }

        using var service = await SerializdServiceFactory
            .CreateAuthenticatedAsync(account.Email, account.Password, _logger)
            .ConfigureAwait(false);

        // 1. Watched-status marking, batched per (show, season).
        foreach (var ((show, season), epNums) in needsWatched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seasonId = await service.ResolveSeasonIdAsync(show, season).ConfigureAwait(false);
                if (seasonId == null)
                {
                    _logger.LogWarning("Serializd has no season {Season} for TMDb show {Show}, skipping", season, show);
                    continue;
                }

                await service.LogEpisodesAsync(show, seasonId.Value, epNums).ConfigureAwait(false);
                foreach (var n in epNums)
                    SerializdSyncHistory.Record(userId, account.Email, show, season, n);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd catch-up: failed marking watched TMDb {Show} S{Season} for {Username}: {Message}",
                    show, season, user.Username, ex.Message);
            }
        }

        // 2. Dated Diary logs, one per episode, backdated to the real watch date.
        SyncProgress.SetPhase(SyncProgress.TrackSerializd, $"Logging {user.Username}'s episodes to Serializd");
        SyncProgress.SetTotal(SyncProgress.TrackSerializd, needsLog.Count);
        var logged = 0;
        foreach (var r in needsLog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncProgress.IncrementProcessed(SyncProgress.TrackSerializd);
            try
            {
                var seasonId = await service.ResolveSeasonIdAsync(r.Show, r.Season).ConfigureAwait(false);
                if (seasonId == null) continue;

                await service.CreateEpisodeLogAsync(r.Show, seasonId.Value, r.Episode, r.WatchedAtUtc, r.Rating, isRewatch: false)
                    .ConfigureAwait(false);
                SerializdSyncHistory.Record(userId, account.Email, r.Show, r.Season, r.Episode, SerializdSyncHistory.KindLog);
                logged++;

                SerializdActivity.Record(new SyncEvent
                {
                    FilmTitle = $"{r.ShowName} · S{r.Season}E{r.Episode}",
                    TmdbId = r.Show,
                    Username = user.Username ?? string.Empty,
                    Timestamp = DateTime.UtcNow,
                    ViewingDate = r.WatchedAtUtc,
                    Status = SyncStatus.Success,
                    Source = source,
                });

                // Be polite during a large first-time backfill.
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd catch-up: failed logging TMDb {Show} S{Season}E{Episode} for {Username}: {Message}",
                    r.Show, r.Season, r.Episode, user.Username, ex.Message);
                SerializdActivity.Record(new SyncEvent
                {
                    FilmTitle = $"{r.ShowName} · S{r.Season}E{r.Episode}",
                    TmdbId = r.Show,
                    Username = user.Username ?? string.Empty,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed,
                    Error = ex.Message,
                    Source = source,
                });

                if (account.StopOnFailure)
                {
                    _logger.LogWarning("Serializd catch-up: stopping on first failure for {Username} (StopOnFailure)", user.Username);
                    break;
                }
            }
        }

        if (logged > 0)
            _logger.LogInformation("Serializd catch-up: created {Count} dated diary logs for {Username} as {Email}",
                logged, user.Username, account.Email);

        // 3. Show-level rating + favorite (like) sync, one entry per rated/favorited series
        //    among the shows we're tracking. is_log:false so it doesn't clutter the Diary.
        await SyncShowMetaAsync(user, userId, records, seriesById, account, service, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncShowMetaAsync(User user, string userId, List<EpisodePlay> records,
        Dictionary<int, Series> seriesById, SerializdAccount account, ISerializdService service,
        CancellationToken cancellationToken)
    {
        var watchedShows = new HashSet<int>();
        foreach (var r in records)
            watchedShows.Add(r.Show);
        if (watchedShows.Count == 0)
            return;

        // The episode scan in SyncOneAsync already resolved each played episode's parent
        // Series; reuse that instead of a second full-library BaseItemKind.Series query.
        foreach (var (tmdb, s) in seriesById)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!watchedShows.Contains(tmdb)) continue;

            var ud = _userDataManager.GetUserData(user, s);
            var rating = SerializdRating.FromJellyfin(ud?.Rating);
            // Favourite → like only when the account opts in (parity with Letterboxd SyncFavorites).
            var favorite = account.SyncFavorites && (ud?.IsFavorite ?? false);
            if (rating == null && !favorite) continue;                 // nothing to sync
            if (SerializdSyncHistory.Has(userId, account.Email, tmdb, 0, 0, SerializdSyncHistory.KindShowMeta)) continue;

            try
            {
                await service.SetShowMetaAsync(tmdb, rating, favorite).ConfigureAwait(false);
                SerializdSyncHistory.Record(userId, account.Email, tmdb, 0, 0, SerializdSyncHistory.KindShowMeta);
                _logger.LogInformation("Serializd: set show meta for TMDb {Show} (rating={Rating}, like={Like}) for {Username}",
                    tmdb, rating, favorite, user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd: failed setting show meta for TMDb {Show} for {Username}: {Message}",
                    tmdb, user.Username, ex.Message);
            }
        }
    }

    private readonly record struct EpisodePlay(int Show, int Season, int Episode, DateTime WatchedAtUtc, int? Rating, string ShowName);

    /// <summary>
    /// Pure: collapse a flat list of (show, season, episode) plays into per-(show, season)
    /// episode lists, dropping any already logged and de-duplicating. Exposed for tests.
    /// </summary>
    internal static Dictionary<(int Show, int Season), List<int>> GroupNewEpisodes(
        IEnumerable<(int Show, int Season, int Episode)> plays,
        Func<int, int, int, bool> alreadyLogged)
    {
        var result = new Dictionary<(int, int), List<int>>();
        var seen = new HashSet<(int, int, int)>();

        foreach (var (show, season, ep) in plays)
        {
            if (alreadyLogged(show, season, ep)) continue;
            if (!seen.Add((show, season, ep))) continue;

            var key = (show, season);
            if (!result.TryGetValue(key, out var list))
            {
                list = new List<int>();
                result[key] = list;
            }

            list.Add(ep);
        }

        foreach (var list in result.Values)
            list.Sort();

        return result;
    }
}
