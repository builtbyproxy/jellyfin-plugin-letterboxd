using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
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
public class LetterboxdController : JellyfinUserApiController
{
    private readonly ILogger<LetterboxdController> _logger;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _appPaths;
    private readonly LetterboxdSyncRunner _syncRunner;
    private readonly WatchlistSyncRunner _watchlistRunner;

    // Holds the most recent fire-and-forget sync task from StartSync/StartWatchlistSync.
    // Production code never reads it; tests await it so a background sync can't outlive
    // its test and hold SyncGate while the next test runs.
    internal Task? LastBackgroundSync { get; private set; }

    public LetterboxdController(
        ILogger<LetterboxdController> logger,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IApplicationPaths appPaths,
        LetterboxdSyncRunner syncRunner,
        WatchlistSyncRunner watchlistRunner)
        : base(userManager)
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
        LastBackgroundSync = Task.Run(async () =>
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
    /// Trigger a Letterboxd → Jellyfin playlist (and optional Seerr) watchlist sync
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

        LastBackgroundSync = Task.Run(async () =>
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

    /// <summary>
    /// Returns the exact JSON the next telemetry ping would send. Admin-only: the payload
    /// contains the instance UUID plus a configuration fingerprint, the same policy as
    /// the config page that displays it. Backs the settings "Preview exact JSON" modal,
    /// which doubles as a copy-diagnostic-bundle for bug reports.
    /// </summary>
    [HttpGet("Telemetry/Preview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTelemetryPreview()
    {
        int? libraryCount;
        try
        {
            libraryCount = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            }).Count;
        }
        catch
        {
            libraryCount = null;
        }

        var json = TelemetryService.BuildPayload("weekly", libraryCount);
        return Content(json, "application/json");
    }

    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var jellyfinUsername = GetJellyfinUsername();
        var (total, success, failed, skipped, rewatches, requested) = SyncHistory.GetStats(jellyfinUsername);
        return Ok(new
        {
            total,
            success,
            failed,
            skipped,
            rewatches,
            requested,
            watchlist = WatchlistStats.GetFilm(GetCurrentUserId() ?? string.Empty)
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
            backfillAvailableRequests = account.BackfillAvailableRequests,
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
        account.BackfillAvailableRequests = request.BackfillAvailableRequests;
        account.MirrorJellyseerrWatchlist = request.MirrorJellyseerrWatchlist;
        account.SkipPreviouslySynced = request.SkipPreviouslySynced;
        account.StopOnFailure = request.StopOnFailure;

        // IsPrimary and PlaylistName are deliberately NOT copied from the request.
        // The userPage form does not expose them; deserialisation would set them to
        // their type defaults (false / null) and clobber values that only the admin
        // config page sets. Treat them as admin-managed and let NormalisePrimaryFlags
        // promote a new account to primary when it's the user's only enabled one.
        Config.NormalisePrimaryFlags();

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation("User {UserId} saved their Letterboxd account settings", userId);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Returns every Letterboxd account belonging to the calling Jellyfin user, in
    /// config order with primary first. Multi-account companion to the single-account
    /// /Account endpoint: the userPage uses this to render the full list of accounts
    /// the user can edit on their own sidebar page.
    /// </summary>
    [HttpGet("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetAccounts()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var accounts = Config.Accounts
            .Where(a => a.UserJellyfinId == userId)
            .OrderByDescending(a => a.IsPrimary)
            .Select(a => new
            {
                letterboxdUsername = a.LetterboxdUsername,
                letterboxdPassword = a.LetterboxdPassword,
                rawCookies = a.RawCookies,
                userAgent = a.UserAgent,
                enabled = a.Enabled,
                syncFavorites = a.SyncFavorites,
                enableDateFilter = a.EnableDateFilter,
                dateFilterDays = a.DateFilterDays,
                enableWatchlistSync = a.EnableWatchlistSync,
                enableDiaryImport = a.EnableDiaryImport,
                autoRequestWatchlist = a.AutoRequestWatchlist,
                backfillAvailableRequests = a.BackfillAvailableRequests,
                mirrorJellyseerrWatchlist = a.MirrorJellyseerrWatchlist,
                skipPreviouslySynced = a.SkipPreviouslySynced,
                stopOnFailure = a.StopOnFailure,
                isPrimary = a.IsPrimary,
                playlistName = a.PlaylistName
            })
            .ToList();

        return Ok(new { accounts });
    }

    /// <summary>
    /// Bulk-replace the calling user's set of Letterboxd accounts. Accounts owned by
    /// other Jellyfin users are preserved (security: a non-admin user cannot touch
    /// another user's row). Each submitted account is stamped with the calling user's
    /// id regardless of what the request body claimed. NormalisePrimaryFlags runs after
    /// so the single-primary-per-user invariant holds across the save.
    /// </summary>
    [HttpPut("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PutAccounts([FromBody] AccountsUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (request?.Accounts == null)
            return BadRequest(new { error = "accounts is required" });

        // Reject empty username up front so a malformed row doesn't silently produce
        // a half-broken account that fails authentication later.
        for (var i = 0; i < request.Accounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request.Accounts[i].LetterboxdUsername))
                return BadRequest(new { error = $"Account #{i + 1} is missing a Letterboxd username" });
        }

        // Preserve every account that doesn't belong to the calling user. The admin
        // page is the only path that should touch other users' rows; this endpoint
        // is per-user scope.
        var preserved = Config.Accounts.Where(a => a.UserJellyfinId != userId).ToList();

        var mine = new List<Account>();
        foreach (var req in request.Accounts)
        {
            mine.Add(new Account
            {
                UserJellyfinId = userId,
                LetterboxdUsername = req.LetterboxdUsername,
                LetterboxdPassword = req.LetterboxdPassword,
                RawCookies = req.RawCookies,
                UserAgent = req.UserAgent,
                Enabled = req.Enabled,
                SyncFavorites = req.SyncFavorites,
                EnableDateFilter = req.EnableDateFilter,
                DateFilterDays = req.DateFilterDays,
                EnableWatchlistSync = req.EnableWatchlistSync,
                EnableDiaryImport = req.EnableDiaryImport,
                AutoRequestWatchlist = req.AutoRequestWatchlist,
                BackfillAvailableRequests = req.BackfillAvailableRequests,
                MirrorJellyseerrWatchlist = req.MirrorJellyseerrWatchlist,
                SkipPreviouslySynced = req.SkipPreviouslySynced,
                StopOnFailure = req.StopOnFailure,
                IsPrimary = req.IsPrimary,
                PlaylistName = string.IsNullOrWhiteSpace(req.PlaylistName) ? null : req.PlaylistName.Trim()
            });
        }

        Config.Accounts.Clear();
        Config.Accounts.AddRange(preserved);
        Config.Accounts.AddRange(mine);

        Config.NormalisePrimaryFlags();
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation("User {UserId} saved {Count} Letterboxd account(s) via /Accounts", userId, mine.Count);
        return Ok(new { success = true, count = mine.Count });
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
        if (!SeerrClient.IsConfigured(request.Url, request.ApiKey))
            return BadRequest(new { success = false, error = "URL and API key are required" });

        try
        {
            using var client = new SeerrClient(request.Url!, request.ApiKey!, _logger);
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
            _logger.LogWarning("Seerr test failed: {Message}", ex.Message);
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
    [Authorize(Policy = "RequiresElevation")] // raw server logs name every user's Letterboxd account + watched films; admin-only
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] int maxLines = 500)
    {
        var cap = Math.Min(Math.Max(maxLines, 1), 5000);
        var (all, source, error) = ReadRecentLogLines();
        if (error != null)
            return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error });

        var trimmed = all.Count > cap ? all.GetRange(all.Count - cap, cap) : all;
        return Ok(new { lines = trimmed, totalMatches = all.Count, returned = trimmed.Count, source });
    }

    // Matches a Jellyfin log-entry header start: "[2026-06-14 ...". Continuation lines
    // (stack frames, exception messages) do not begin this way.
    private static readonly System.Text.RegularExpressions.Regex _logHeader =
        new(@"^\[\d{4}-\d{2}-\d{2}", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ANSI CSI escape sequences (colour codes) that some console sinks emit.
    private static readonly System.Text.RegularExpressions.Regex _ansi =
        new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsLogHeader(string line) => _logHeader.IsMatch(line);

    private static string StripAnsi(string line) => _ansi.Replace(line, string.Empty);

    /// <summary>
    /// Reads ALL recent LetterboxdSync-tagged lines from Jellyfin's two newest main
    /// log files (covering a just-rolled-over file), untrimmed. Shared by the Logs tab
    /// and the "Send logs to developer" bundle; each caller caps as it sees fit. The
    /// plugin never logs auth tokens, passwords, cookies, or review text, so these
    /// lines are safe to share.
    /// </summary>
    private (List<string> Lines, string? Source, string? Error) ReadRecentLogLines()
    {
        try
        {
            var logDir = _appPaths.LogDirectoryPath;
            if (!Directory.Exists(logDir))
                return (new List<string>(), null, "log directory not found");

            var mainLogs = Directory.GetFiles(logDir, "log_*.log")
                .OrderByDescending(f => f)
                .Take(2)
                .ToList();
            if (mainLogs.Count == 0)
                return (new List<string>(), null, "no log files");

            var lines = new List<string>();
            foreach (var path in mainLogs.AsEnumerable().Reverse())
            {
                // Force UTF-8 so non-ASCII (accented/CJK film titles) round-trips cleanly.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                string? line;
                // A matched LetterboxdSync entry is often multi-line: the header line carries
                // the tag, but the exception message and "   at ..." stack frames continue on
                // following lines that DON'T carry the tag. Capture those continuation lines
                // too (until the next timestamped header) or the most useful diagnostic, the
                // stack trace, gets shredded by a per-line filter.
                var inMatch = false;
                while ((line = sr.ReadLine()) != null)
                {
                    line = StripAnsi(line);
                    var isHeader = IsLogHeader(line);
                    if (isHeader)
                    {
                        inMatch = line.Contains("LetterboxdSync", StringComparison.Ordinal) ||
                                  line.Contains("Letterboxd ", StringComparison.Ordinal);
                        if (inMatch) lines.Add(line);
                    }
                    else if (inMatch)
                    {
                        lines.Add(line); // continuation of a matched entry (stack frame / message)
                    }
                }
            }

            return (lines, string.Join(", ", mainLogs.Select(Path.GetFileName)), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ReadRecentLogLines failed: {Message}", ex.Message);
            return (new List<string>(), null, ex.Message);
        }
    }

    /// <summary>
    /// User-initiated "send logs to developer". Builds a diagnostic bundle (recent
    /// sanitized log lines + the current telemetry snapshot + versions) and uploads
    /// it to the private telemetry backend, returning a short reference code the user
    /// can quote in a bug report. Admin-only. Unlike telemetry this is NOT anonymous
    /// (logs may contain a Letterboxd username or film titles) and only runs on this
    /// explicit, disclosed action. Works whether or not telemetry is enabled; if no
    /// telemetry instance id exists, a one-off id is generated for the bundle.
    /// </summary>
    /// <summary>
    /// Assembles the exact diagnostic bundle JSON for the calling server. Used by both
    /// the preview and the send so they cannot diverge: what the preview shows is byte
    /// for byte what the send uploads (note aside, which the user types in either path).
    /// </summary>
    private (string Json, int MatchedLines) BuildLogBundleJson(string? note)
    {
        var (allLines, source, error) = ReadRecentLogLines();
        var matched = allLines.Count;
        var lines = allLines.Count > 500 ? allLines.GetRange(allLines.Count - 500, 500) : allLines;

        // Collector status travels inside the bundle. Without it an empty capture is
        // indistinguishable (server-side) from a broken collector: bundle LBX-C1EP38
        // arrived as log_lines=[] with nothing saying whether the plugin was idle,
        // the log dir was missing, or the line filter matched nothing.
        lines.Insert(0, $"[meta] collector: files={source ?? "none"}; matched={matched}; error={error ?? "none"}");
        var collector = new { files = source ?? "none", matched, error = error ?? "none" };

        int? libraryCount;
        try
        {
            libraryCount = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            }).Count;
        }
        catch
        {
            libraryCount = null;
        }

        var telemetrySnapshot = TelemetryService.BuildPayload("logs", libraryCount);
        var instanceId = Plugin.Instance?.Configuration?.Telemetry?.InstanceId;
        if (string.IsNullOrEmpty(instanceId))
            instanceId = Guid.NewGuid().ToString();

        var json = TelemetryService.BuildLogBundleJson(
            instanceId,
            Plugin.Instance?.Version?.ToString() ?? "unknown",
            telemetrySnapshot,
            note,
            lines,
            collector);
        return (json, matched);
    }

    /// <summary>
    /// Returns the EXACT bundle that "Send logs to developer" would upload, without
    /// sending it. Backs the consent modal's "Preview exactly what's sent" so the user
    /// sees the real log lines and telemetry snapshot, not just the anonymous part.
    /// </summary>
    [HttpGet("Telemetry/PreviewLogs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult PreviewLogs()
    {
        return Content(BuildLogBundleJson(null).Json, "application/json");
    }

    [HttpPost("Telemetry/SendLogs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SendLogs([FromBody] SendLogsRequest? request)
    {
        var (json, matched) = BuildLogBundleJson(request?.Note);
        var code = await TelemetryService.PostLogBundleAsync(json).ConfigureAwait(false);

        if (string.IsNullOrEmpty(code))
            return BadRequest(new { error = "Could not reach the diagnostics endpoint. Check the server's internet connection and try again." });

        // Still a success (the telemetry snapshot alone can be useful), but the user
        // must know their diagnostics were blank, otherwise they quote the ref code
        // in a bug report and wait on logs that never arrived.
        string? warning = matched == 0
            ? "No LetterboxdSync entries were found in the two newest server log files, so the bundle contains no log lines. Reproduce the problem first (for example, run a sync), then send logs again."
            : null;

        return Ok(new { refCode = code, warning });
    }

    /// <summary>
    /// Returns the calling user's stored Jellyfin rating for an item, both raw
    /// (1-10) and mapped to Letterboxd half-stars, so the review modal can
    /// pre-fill its star widget. Movies resolve by TMDb id; TV resolves the
    /// series by TMDb id, or a single episode when season and episode numbers
    /// are supplied (matching how Serializd sync sources episode vs show
    /// ratings). Always 200 with null fields when the item or rating is absent:
    /// the pre-fill fetch must never surface an error in the modal.
    /// </summary>
    [HttpGet("ItemRating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetItemRating(
        [FromQuery] int? tmdbId = null,
        [FromQuery] bool isShow = false,
        [FromQuery] int? seasonNumber = null,
        [FromQuery] int? episodeNumber = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var none = Ok(new { rating = (double?)null, stars = (double?)null });
        if (!tmdbId.HasValue || tmdbId.Value <= 0)
            return none;

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return none;

        BaseItem? item;
        if (!isShow)
            item = FindMovieByTmdbId(user, tmdbId.Value);
        else if (seasonNumber.HasValue && episodeNumber.HasValue)
            item = FindEpisodeByTmdbId(user, tmdbId.Value, seasonNumber.Value, episodeNumber.Value);
        else
            item = FindSeriesByTmdbId(user, tmdbId.Value);

        if (item == null)
            return none;

        var stored = _userDataManager.GetUserData(user, item)?.Rating;
        if (!stored.HasValue || stored.Value <= 0)
            return none;

        return Ok(new { rating = (double?)stored.Value, stars = Helpers.MapRating(stored.Value) });
    }

    /// <summary>
    /// Resolve a library movie by TMDb id for the given user. Shared by the
    /// rating writeback and the ItemRating read path so the two cannot drift.
    /// </summary>
    private BaseItem? FindMovieByTmdbId(User user, int tmdbId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        }).FirstOrDefault(m => m.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) == tmdbId.ToString());
    }

    private BaseItem? FindSeriesByTmdbId(User user, int tmdbId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true
        }).FirstOrDefault(s => s.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) == tmdbId.ToString());
    }

    /// <summary>
    /// Resolve an episode by series TMDb id + season/episode number. The series
    /// TMDb id lives on the parent Series entity; SeriesTmdbIdReader is the one
    /// place that knows that, shared with Serializd sync.
    /// </summary>
    private BaseItem? FindEpisodeByTmdbId(User user, int seriesTmdbId, int seasonNumber, int episodeNumber)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        }).OfType<MediaBrowser.Controller.Entities.TV.Episode>()
          .FirstOrDefault(ep => Serializd.SerializdSyncRunner.SeriesTmdbIdReader(ep) == seriesTmdbId
              && ep.ParentIndexNumber == seasonNumber
              && ep.IndexNumber == episodeNumber);
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

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return;

        var movie = FindMovieByTmdbId(user, tmdbId.Value);

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

public class SendLogsRequest
{
    /// <summary>Optional free-text note from the user describing what went wrong.</summary>
    public string? Note { get; set; }
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
