using System;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
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

    public LetterboxdController(ILogger<LetterboxdController> logger, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager)
    {
        _logger = logger;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var (total, success, failed, skipped, rewatches) = SyncHistory.GetStats();
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
        var events = SyncHistory.GetRecent(Math.Min(count, 200));
        return Ok(events);
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

        var userId = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var account = Config.Accounts.FirstOrDefault(
            a => a.Enabled && a.UserJellyfinId == userId.Replace("-", ""));

        if (account == null)
            return BadRequest(new { error = "No Letterboxd account configured for this user" });

        using var client = new LetterboxdClient(_logger);
        try
        {
            await client.AuthenticateAsync(account).ConfigureAwait(false);

            if (client.TokensRefreshed)
            {
                Plugin.Instance!.SaveConfiguration();
            }

            int? tmdbId = null;
            var historyMatch = SyncHistory.GetRecent(500).FirstOrDefault(h => h.FilmSlug == request.FilmSlug && h.TmdbId > 0);
            if (historyMatch != null)
                tmdbId = historyMatch.TmdbId;

            if (!tmdbId.HasValue)
                return BadRequest(new { error = "Cannot find TMDb ID for this film. Please play the film briefly to sync it to your history first." });

            var film = await client.LookupFilmByTmdbIdAsync(tmdbId.Value).ConfigureAwait(false);

            await client.PostReviewAsync(film.FilmId, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, request.Date, request.Rating)
                .ConfigureAwait(false);

            _logger.LogInformation("Posted review for {FilmSlug} by {Username}",
                request.FilmSlug, account.LetterboxdUsername);

            var status = request.IsRewatch ? SyncStatus.Rewatch : SyncStatus.Success;
            var label = request.IsRewatch ? "Rewatch + review" : "Review";
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = request.FilmSlug.Replace("-", " "),
                FilmSlug = request.FilmSlug,
                Username = account.LetterboxdUsername,
                Timestamp = DateTime.UtcNow,
                Status = status,
                Source = "review"
            });

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to post review for {FilmSlug}: {Message}", request.FilmSlug, ex.Message);

            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = request.FilmSlug.Replace("-", " "),
                FilmSlug = request.FilmSlug,
                Username = account?.LetterboxdUsername ?? "unknown",
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Failed,
                Error = ex.Message,
                Source = "review"
            });

            return BadRequest(new { error = ex.Message });
        }
    }
    [HttpPost("Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PostRetry([FromBody] RetryRequest request)
    {
        if (request.TmdbId <= 0)
            return BadRequest(new { error = "tmdbId is required" });

        var userId = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var user = _userManager.GetUserById(new Guid(userId));
        if (user == null)
            return BadRequest(new { error = "Invalid Jellyfin user" });

        var account = Config.Accounts.FirstOrDefault(
            a => a.Enabled && a.UserJellyfinId == userId.Replace("-", ""));

        if (account == null)
            return BadRequest(new { error = "No Letterboxd account configured for this user" });

        using var client = new LetterboxdClient(_logger);
        try
        {
            await client.AuthenticateAsync(account).ConfigureAwait(false);

            if (client.TokensRefreshed)
            {
                Plugin.Instance!.SaveConfiguration();
            }

            var movie = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).FirstOrDefault(m => m.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) == request.TmdbId.ToString());

            if (movie == null)
                return NotFound(new { error = "Cannot find movie in Jellyfin library with that TMDb ID." });

            var film = await client.LookupFilmByTmdbIdAsync(request.TmdbId).ConfigureAwait(false);
            var diary = await client.GetDiaryInfoAsync(film.FilmId).ConfigureAwait(false);

            var userData = _userDataManager.GetUserData(user, movie);
            var viewingDate = userData?.LastPlayedDate?.Date ?? DateTime.Now.Date;

            bool isRewatch = diary.IsWatched || diary.HasAnyEntry;
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
                    _logger.LogWarning("Failed to sync like status for {Title} ({FilmId}): {Message}", movie.Name, film.FilmId, likeEx.Message);
                }
            }

            _logger.LogInformation("Manually retried sync for {Title} by {Username}",
                movie.Name, account.LetterboxdUsername);

            var status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success;
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = movie.Name,
                FilmSlug = film.Slug,
                TmdbId = request.TmdbId,
                Username = account.LetterboxdUsername,
                Timestamp = DateTime.UtcNow,
                ViewingDate = viewingDate,
                Status = status,
                Source = "retry"
            });

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to manually retry sync for TMDb {TmdbId}: {Message}", request.TmdbId, ex.Message);

            string filmTitle = $"TMDb ID: {request.TmdbId}";
            var oldEvent = SyncHistory.GetRecent(500).FirstOrDefault(e => e.TmdbId == request.TmdbId);
            if (oldEvent != null && !string.IsNullOrEmpty(oldEvent.FilmTitle) && !oldEvent.FilmTitle.StartsWith("TMDb ID"))
            {
                filmTitle = oldEvent.FilmTitle;
            }

            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = filmTitle,
                TmdbId = request.TmdbId,
                Username = account?.LetterboxdUsername ?? "unknown",
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Failed,
                Error = ex.Message,
                Source = "retry"
            });

            if (ex.Message.Contains("Could not find film matching"))
                return NotFound(new { error = ex.Message });

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
}

public class RetryRequest
{
    public int TmdbId { get; set; }
}
