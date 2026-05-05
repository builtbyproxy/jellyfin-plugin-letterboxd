using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync")]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdController : ControllerBase
{
    private readonly ILogger<LetterboxdController> _logger;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _appPaths;
    private readonly LetterboxdSyncRunner _syncRunner;
    private readonly WatchlistSyncRunner _watchlistRunner;

    public LetterboxdController(
        ILogger<LetterboxdController> logger,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IApplicationPaths appPaths,
        LetterboxdSyncRunner syncRunner,
        WatchlistSyncRunner watchlistRunner)
    {
        _logger = logger;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _appPaths = appPaths;
        _syncRunner = syncRunner;
        _watchlistRunner = watchlistRunner;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private string? GetCurrentUserId()
        => User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value?.Replace("-", "");

    private string? GetJellyfinUsername()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return null;

        // Resolve Jellyfin user ID to username for SyncHistory filtering
        // SyncHistory stores the Jellyfin username (e.g. "lachlan"), not the Letterboxd username
        var user = _userManager.Users.FirstOrDefault(u => u.Id.ToString("N") == userId);
        return user?.Username;
    }

    [HttpGet("Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetProgress()
    {
        return Ok(SyncProgress.GetSnapshot());
    }

    /// <summary>
    /// Trigger a sync for the calling user. Any logged-in user can call this; it only ever
    /// touches their own Letterboxd account. Returns 202 immediately and runs in the background;
    /// the UI polls /Progress for completion. Returns 409 if a sync is already running.
    /// </summary>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // When letterboxdUsername is supplied, target only that account. Otherwise the
        // runner fans out across all enabled accounts for this Jellyfin user.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            if (Config.FindAccount(userId, letterboxdUsername) == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
        }
        else if (!Config.GetEnabledAccountsForUser(userId).Any())
        {
            return BadRequest(new { error = "No enabled Letterboxd accounts are configured for your user" });
        }

        // Fire and forget. Errors during the run are logged by the runner; the UI polls /Progress.
        _ = Task.Run(async () =>
        {
            try
            {
                await _syncRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    /// <summary>
    /// Trigger a Letterboxd → Jellyfin playlist (and optional Jellyseerr) watchlist sync
    /// for the calling user. Same shape as <see cref="StartSync"/>: 202 immediately,
    /// 409 if any sync is in flight, 400 if the user has no watchlist-enabled account.
    /// </summary>
    [HttpPost("SyncWatchlist")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartWatchlistSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // Validate up front so we can return a 400 instead of starting an empty run.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            var account = Config.FindAccount(userId, letterboxdUsername);
            if (account == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
            if (!account.EnableWatchlistSync)
                return BadRequest(new { error = $"Watchlist sync is disabled for '{letterboxdUsername}'; enable it in Settings first" });
        }
        else
        {
            var enabled = Config.GetEnabledAccountsForUser(userId).Where(a => a.EnableWatchlistSync).ToList();
            if (enabled.Count == 0)
                return BadRequest(new { error = "No enabled accounts with watchlist sync turned on for your user" });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _watchlistRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered watchlist sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var jellyfinUsername = GetJellyfinUsername();
        var (total, success, failed, skipped, rewatches) = SyncHistory.GetStats(jellyfinUsername);
        return Ok(new
        {
            total,
            success,
            failed,
            skipped,
            rewatches
        });
    }

    /// <summary>
    /// Paginated sync history. Returns the slice plus the total so the dashboard can
    /// render a paginator. Without an offset the response is backwards-compatible with
    /// pre-pagination clients that just consumed the array, since callers that ignore
    /// the wrapper still get the most recent items via /History?count=N.
    /// </summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetHistory([FromQuery] int count = 50, [FromQuery] int offset = 0)
    {
        var jellyfinUsername = GetJellyfinUsername();
        var capped = Math.Min(Math.Max(count, 1), 200);
        var (events, total) = SyncHistory.GetPage(Math.Max(offset, 0), capped, jellyfinUsername);
        return Ok(new { events, total, offset = Math.Max(offset, 0), count = capped });
    }

    [HttpGet("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetAccount()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var account = Config.Accounts.FirstOrDefault(a => a.UserJellyfinId == userId);
        if (account == null)
        {
            return Ok(new
            {
                letterboxdUsername = string.Empty,
                letterboxdPassword = string.Empty,
                rawCookies = (string?)null,
                userAgent = (string?)null,
                enabled = false,
                syncFavorites = false,
                enableDateFilter = false,
                dateFilterDays = 7,
                enableWatchlistSync = false,
                enableDiaryImport = false,
                autoRequestWatchlist = false,
                mirrorJellyseerrWatchlist = false,
                skipPreviouslySynced = true,
                stopOnFailure = false,
                isConfigured = false
            });
        }

        return Ok(new
        {
            letterboxdUsername = account.LetterboxdUsername,
            letterboxdPassword = account.LetterboxdPassword,
            rawCookies = account.RawCookies,
            userAgent = account.UserAgent,
            enabled = account.Enabled,
            syncFavorites = account.SyncFavorites,
            enableDateFilter = account.EnableDateFilter,
            dateFilterDays = account.DateFilterDays,
            enableWatchlistSync = account.EnableWatchlistSync,
            enableDiaryImport = account.EnableDiaryImport,
            autoRequestWatchlist = account.AutoRequestWatchlist,
            mirrorJellyseerrWatchlist = account.MirrorJellyseerrWatchlist,
            skipPreviouslySynced = account.SkipPreviouslySynced,
            stopOnFailure = account.StopOnFailure,
            isConfigured = true
        });
    }

    [HttpPut("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PutAccount([FromBody] AccountUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var account = Config.Accounts.FirstOrDefault(a => a.UserJellyfinId == userId);
        if (account == null)
        {
            account = new Account { UserJellyfinId = userId };
            Config.Accounts.Add(account);
        }

        account.LetterboxdUsername = request.LetterboxdUsername;
        account.LetterboxdPassword = request.LetterboxdPassword;
        account.RawCookies = request.RawCookies;
        account.UserAgent = request.UserAgent;
        account.Enabled = request.Enabled;
        account.SyncFavorites = request.SyncFavorites;
        account.EnableDateFilter = request.EnableDateFilter;
        account.DateFilterDays = request.DateFilterDays;
        account.EnableWatchlistSync = request.EnableWatchlistSync;
        account.EnableDiaryImport = request.EnableDiaryImport;
        account.AutoRequestWatchlist = request.AutoRequestWatchlist;
        account.MirrorJellyseerrWatchlist = request.MirrorJellyseerrWatchlist;
        account.SkipPreviouslySynced = request.SkipPreviouslySynced;
        account.StopOnFailure = request.StopOnFailure;
        account.IsPrimary = request.IsPrimary;
        account.PlaylistName = request.PlaylistName;

        // Keep the single-primary-per-user invariant after the user toggles flags.
        Config.NormalisePrimaryFlags();

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation("User {UserId} saved their Letterboxd account settings", userId);

        return Ok(new { success = true });
    }

    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestConnection([FromBody] TestConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LetterboxdUsername) || string.IsNullOrWhiteSpace(request.LetterboxdPassword))
            return BadRequest(new { success = false, error = "Username and password are required" });

        try
        {
            using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                request.LetterboxdUsername, request.LetterboxdPassword, request.RawCookies, _logger, request.UserAgent)
                .ConfigureAwait(false);

            return Ok(new { success = true, letterboxdUsername = request.LetterboxdUsername });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Test connection failed for {Username}: {Message}", request.LetterboxdUsername, ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("TestJellyseerr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestJellyseerr([FromBody] JellyseerrTestRequest request)
    {
        if (!JellyseerrClient.IsConfigured(request.Url, request.ApiKey))
            return BadRequest(new { success = false, error = "URL and API key are required" });

        try
        {
            using var client = new JellyseerrClient(request.Url!, request.ApiKey!, _logger);
            var userId = await client.GetJellyseerrUserIdAsync(GetCurrentUserId() ?? string.Empty)
                .ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                linkedToCurrentUser = userId.HasValue,
                jellyseerrUserId = userId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Jellyseerr test failed: {Message}", ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("Review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> PostReview([FromBody] ReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilmSlug))
            return BadRequest(new { error = "filmSlug is required" });

        if (string.IsNullOrWhiteSpace(request.ReviewText) && !request.IsRewatch)
            return BadRequest(new { error = "reviewText is required unless logging a rewatch" });

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        // When LetterboxdUsername is set, post under that single account. When it's
        // null/empty, fan out to every enabled account for this Jellyfin user, so
        // shared TV-user setups (e.g. Lachlan + Deb) get the review on both diaries.
        List<Account> accounts;
        if (!string.IsNullOrWhiteSpace(request.LetterboxdUsername))
        {
            var single = Config.FindAccount(userId, request.LetterboxdUsername!);
            if (single == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{request.LetterboxdUsername}' for this user" });
            accounts = new List<Account> { single };
        }
        else
        {
            accounts = Config.GetEnabledAccountsForUser(userId).ToList();
            if (accounts.Count == 0)
                return BadRequest(new { error = "No Letterboxd accounts configured for this user" });
        }

        var jellyfinUsername = GetJellyfinUsername() ?? userId;
        var perAccount = new List<object>();
        var anySuccess = false;
        Exception? lastError = null;

        foreach (var account in accounts)
        {
            try
            {
                using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                    account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                    .ConfigureAwait(false);

                await service.PostReviewAsync(request.FilmSlug, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, request.Date, request.Rating, request.TmdbId)
                    .ConfigureAwait(false);

                _logger.LogInformation("Posted review for {FilmSlug} by {Username}",
                    request.FilmSlug, account.LetterboxdUsername);

                var status = request.IsRewatch ? SyncStatus.Rewatch : SyncStatus.Success;
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    Username = jellyfinUsername,
                    Timestamp = DateTime.UtcNow,
                    Status = status,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = true });
                anySuccess = true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogError("Failed to post review for {FilmSlug} as {LbUser}: {Message}",
                    request.FilmSlug, account.LetterboxdUsername, ex.Message);

                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    Username = jellyfinUsername,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed,
                    Error = ex.Message,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = false, error = ex.Message });
            }
        }

        // Mirror the rating to Jellyfin once if any account accepted it; the rating
        // belongs to the Jellyfin user, not to a specific Letterboxd account, so a
        // single writeback is correct regardless of how many accounts we posted to.
        if (anySuccess)
            WriteJellyfinRating(userId, request.TmdbId, request.Rating);

        if (!anySuccess && lastError != null)
            return BadRequest(new { error = lastError.Message, accounts = perAccount });

        return Ok(new { success = true, accounts = perAccount });
    }

    /// <summary>
    /// Returns the most recent LetterboxdSync log lines from Jellyfin's log files,
    /// for in-dashboard debugging and "send me your logs" support flows.
    /// Reads only LetterboxdSync-tagged lines so users can share without leaking
    /// unrelated server activity. The plugin already redacts review text and never
    /// logs auth tokens, passwords, or cookies, so this output is safe to share.
    /// </summary>
    [HttpGet("Logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] int maxLines = 500)
    {
        try
        {
            var logDir = _appPaths.LogDirectoryPath;
            if (!Directory.Exists(logDir))
                return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error = "log directory not found" });

            // Look at the two most recent main log files. Cover the case where the
            // current file just rolled over and recent activity is in the previous one.
            var mainLogs = Directory.GetFiles(logDir, "log_*.log")
                .OrderByDescending(f => f)
                .Take(2)
                .ToList();

            if (mainLogs.Count == 0)
                return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error = "no log files" });

            var lines = new List<string>();
            foreach (var path in mainLogs.AsEnumerable().Reverse())
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Contains("LetterboxdSync", StringComparison.Ordinal) ||
                        line.Contains("Letterboxd ", StringComparison.Ordinal))
                    {
                        lines.Add(line);
                    }
                }
            }

            // Cap to last N lines so the response stays small.
            var trimmed = lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
            return Ok(new
            {
                lines = trimmed,
                totalMatches = lines.Count,
                returned = trimmed.Count,
                source = string.Join(", ", mainLogs.Select(Path.GetFileName))
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("GetLogs failed: {Message}", ex.Message);
            return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error = ex.Message });
        }
    }

    /// <summary>
    /// Mirror the dashboard review's star rating into Jellyfin's UserItemData.Rating
    /// so it survives plugin uninstall and is visible to other clients/plugins.
    /// Always overwrites: posting a review is the user's latest input, so it wins.
    /// No-op if the review had no rating, no TmdbId, or the film isn't in the library.
    /// </summary>
    private void WriteJellyfinRating(string userId, int? tmdbId, double? letterboxdRating)
    {
        if (!tmdbId.HasValue || !letterboxdRating.HasValue)
            return;

        var jellyfinRating = Helpers.LetterboxdToJellyfinRating(letterboxdRating);
        if (!jellyfinRating.HasValue)
            return;

        var user = _userManager.Users.FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return;

        var movie = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        }).FirstOrDefault(m => m.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) == tmdbId.Value.ToString());

        if (movie == null)
        {
            _logger.LogDebug("Skipping Jellyfin rating writeback: TMDb {TmdbId} not in library", tmdbId.Value);
            return;
        }

        try
        {
            var userData = _userDataManager.GetUserData(user, movie);
            if (userData == null) return;

            userData.Rating = jellyfinRating;
            _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

            _logger.LogInformation("Mirrored Letterboxd rating {LbRating} -> Jellyfin {JfRating} for {Title} ({UserId})",
                letterboxdRating.Value, jellyfinRating.Value, movie.Name, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to write Jellyfin rating for TMDb {TmdbId}: {Message}", tmdbId.Value, ex.Message);
        }
    }
}

public class ReviewRequest
{
    public string FilmSlug { get; set; } = string.Empty;
    public string? ReviewText { get; set; }
    public bool ContainsSpoilers { get; set; }
    public bool IsRewatch { get; set; }
    public string? Date { get; set; }
    public double? Rating { get; set; }
    public int? TmdbId { get; set; }

    /// <summary>
    /// Optional. When set, the review is posted only to that Letterboxd account.
    /// When null/empty, the review is fanned out to every enabled Letterboxd account
    /// for the calling Jellyfin user.
    /// </summary>
    public string? LetterboxdUsername { get; set; }
}
