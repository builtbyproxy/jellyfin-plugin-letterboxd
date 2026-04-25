using System;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Library;
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

    public LetterboxdController(ILogger<LetterboxdController> logger, IUserManager userManager)
    {
        _logger = logger;
        _userManager = userManager;
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

    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetHistory([FromQuery] int count = 50)
    {
        var jellyfinUsername = GetJellyfinUsername();
        var events = SyncHistory.GetRecent(Math.Min(count, 200), jellyfinUsername);
        return Ok(events);
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

        var account = Config.Accounts.FirstOrDefault(
            a => a.Enabled && a.UserJellyfinId == userId);

        if (account == null)
            return BadRequest(new { error = "No Letterboxd account configured for this user" });

        try
        {
            using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                .ConfigureAwait(false);

            await service.PostReviewAsync(request.FilmSlug, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, request.Date, request.Rating, request.TmdbId)
                .ConfigureAwait(false);

            _logger.LogInformation("Posted review for {FilmSlug} by {Username}",
                request.FilmSlug, account.LetterboxdUsername);

            var jellyfinUsername = GetJellyfinUsername() ?? account.LetterboxdUsername;
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

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to post review for {FilmSlug}: {Message}", request.FilmSlug, ex.Message);

            var jellyfinUsernameForError = GetJellyfinUsername() ?? account.LetterboxdUsername;
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = request.FilmSlug.Replace("-", " "),
                FilmSlug = request.FilmSlug,
                Username = jellyfinUsernameForError,
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Failed,
                Error = ex.Message,
                Source = "review"
            });

            return BadRequest(new { error = ex.Message });
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
}
