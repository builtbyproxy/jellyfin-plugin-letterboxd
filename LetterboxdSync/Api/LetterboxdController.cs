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
            client.SetRawCookies(account.RawCookies);
            await client.AuthenticateAsync(account.LetterboxdUsername, account.LetterboxdPassword)
                .ConfigureAwait(false);

            await client.PostReviewAsync(request.FilmSlug, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, request.Date, request.Rating)
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
