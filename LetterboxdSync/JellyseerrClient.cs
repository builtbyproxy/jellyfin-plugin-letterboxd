using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Lightweight Jellyseerr client for fetching the Jellyfin-user-to-Jellyseerr-user
/// mapping, creating per-user movie requests, and mirroring a user's watchlist.
/// </summary>
public class JellyseerrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _baseUrl;

    private Dictionary<string, int>? _jellyfinIdToJellyseerrId;

    public JellyseerrClient(string baseUrl, string apiKey, ILogger logger, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
    }

    /// <summary>Returns true if the client looks usable (URL + key set).</summary>
    public static bool IsConfigured(string? url, string? apiKey)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>
    /// Looks up the Jellyseerr user ID corresponding to a Jellyfin user ID (32-char hex).
    /// Returns null if no matching user is found.
    /// </summary>
    public async Task<int?> GetJellyseerrUserIdAsync(string jellyfinUserId)
    {
        if (_jellyfinIdToJellyseerrId == null)
            await LoadUserMapAsync().ConfigureAwait(false);

        var normalized = jellyfinUserId.Replace("-", string.Empty).ToLowerInvariant();
        return _jellyfinIdToJellyseerrId!.TryGetValue(normalized, out var id) ? id : null;
    }

    private async Task LoadUserMapAsync()
    {
        _jellyfinIdToJellyseerrId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var take = 100;
        var skip = 0;
        while (true)
        {
            var url = $"{_baseUrl}/api/v1/user?take={take}&skip={skip}";
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");

            var count = results.GetArrayLength();
            if (count == 0) break;

            foreach (var user in results.EnumerateArray())
            {
                if (!user.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                if (!user.TryGetProperty("jellyfinUserId", out var jfIdEl) || jfIdEl.ValueKind != JsonValueKind.String)
                    continue;
                var jfId = jfIdEl.GetString();
                if (string.IsNullOrWhiteSpace(jfId)) continue;
                var key = jfId.Replace("-", string.Empty).ToLowerInvariant();
                _jellyfinIdToJellyseerrId[key] = idEl.GetInt32();
            }

            skip += count;
            if (count < take) break;
        }

        _logger.LogInformation("Jellyseerr user map loaded: {Count} users with linked Jellyfin IDs",
            _jellyfinIdToJellyseerrId.Count);
    }

    /// <summary>
    /// Looks up the current MediaStatus for a TMDb movie in Jellyseerr.
    /// Returns null when Jellyseerr has no record of the title (so it's safe to request),
    /// or when the call fails (caller should fall through to attempting the request and
    /// let Jellyseerr surface the real error). Status values follow Jellyseerr's enum:
    /// 1=UNKNOWN, 2=PENDING, 3=PROCESSING, 4=PARTIALLY_AVAILABLE, 5=AVAILABLE,
    /// 6=BLOCKLISTED, 7=DELETED.
    /// </summary>
    public async Task<int?> GetMovieMediaStatusAsync(int tmdbId)
    {
        var url = $"{_baseUrl}/api/v1/movie/{tmdbId}";
        try
        {
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jellyseerr movie lookup non-success for TMDb {TmdbId}: {Status}",
                    tmdbId, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mediaInfo", out var mediaInfo) ||
                mediaInfo.ValueKind != JsonValueKind.Object)
                return null;

            if (!mediaInfo.TryGetProperty("status", out var statusEl) ||
                statusEl.ValueKind != JsonValueKind.Number)
                return null;

            return statusEl.GetInt32();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Jellyseerr movie lookup errored for TMDb {TmdbId}: {Message}",
                tmdbId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Outcome of a request attempt. <see cref="AlreadyExists"/> means Jellyseerr already
    /// had the title in some non-UNKNOWN state (pending, processing, available, blocklisted)
    /// and we did NOT POST a new request; it should not be counted as a fresh request.
    /// </summary>
    public enum RequestResult
    {
        Requested,
        AlreadyExists,
        Failed
    }

    /// <summary>
    /// Creates a movie request in Jellyseerr for the given TMDb ID, attributed to the given Jellyseerr user.
    /// Pre-checks the media status and skips the POST when Jellyseerr already has a record of the title;
    /// without this guard Jellyseerr cheerfully creates a duplicate request every run for items already
    /// pending / processing / available, instead of returning 409.
    /// </summary>
    public async Task<RequestResult> RequestMovieAsync(int tmdbId, int jellyseerrUserId)
    {
        var existingStatus = await GetMovieMediaStatusAsync(tmdbId).ConfigureAwait(false);
        // Re-request DELETED (7); skip everything else above UNKNOWN (1).
        if (existingStatus.HasValue && existingStatus.Value > 1 && existingStatus.Value != 7)
        {
            _logger.LogDebug("Skipping Jellyseerr request for TMDb {TmdbId}: already has status {Status}",
                tmdbId, existingStatus.Value);
            return RequestResult.AlreadyExists;
        }

        var url = $"{_baseUrl}/api/v1/request";
        var body = $"{{\"mediaType\":\"movie\",\"mediaId\":{tmdbId},\"userId\":{jellyseerrUserId}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return RequestResult.Requested;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        // Belt-and-braces: Jellyseerr returns 409 when already requested; treat as a no-op.
        if ((int)response.StatusCode == 409 ||
            responseBody.Contains("REQUEST_EXISTS", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already requested", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already available", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Jellyseerr already has request for TMDb {TmdbId} (user {UserId}): {Body}",
                tmdbId, jellyseerrUserId, Truncate(responseBody, 200));
            return RequestResult.AlreadyExists;
        }

        _logger.LogWarning("Jellyseerr request failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return RequestResult.Failed;
    }

    /// <summary>
    /// Returns the set of TMDb IDs of <paramref name="mediaType"/> currently on the given Jellyseerr
    /// user's watchlist. Pages through results until exhausted. Returns an empty set on failure
    /// (caller should treat that as "unknown" and avoid destructive removals).
    /// </summary>
    public async Task<HashSet<int>> GetUserWatchlistTmdbIdsAsync(int jellyseerrUserId, string mediaType = "movie")
    {
        var ids = new HashSet<int>();
        var page = 1;
        var totalPages = 1; // Updated from the first response.

        while (page <= totalPages)
        {
            // Jellyseerr's user/:id/watchlist endpoint paginates with `page=N`. It rejects
            // unknown params with a 400, so do NOT pass take/skip here.
            var url = $"{_baseUrl}/api/v1/user/{jellyseerrUserId}/watchlist?page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-API-User",
                jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("Jellyseerr watchlist fetch failed for user {UserId}: {Status} {Body}",
                    jellyseerrUserId, (int)response.StatusCode, Truncate(errBody, 300));
                return ids;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number)
                totalPages = tp.GetInt32();

            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                break;

            var count = results.GetArrayLength();
            if (count == 0) break;

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String)
                {
                    if (!string.Equals(mt.GetString(), mediaType, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // tmdbId can come back as either a number (DB shape) or a string (Plex shape).
                if (!item.TryGetProperty("tmdbId", out var idEl)) continue;
                int parsed;
                if (idEl.ValueKind == JsonValueKind.Number)
                {
                    parsed = idEl.GetInt32();
                }
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var s))
                {
                    parsed = s;
                }
                else
                {
                    continue;
                }

                ids.Add(parsed);
            }

            page++;
        }

        return ids;
    }

    /// <summary>
    /// Adds a TMDb title to the given Jellyseerr user's watchlist. Acts as that user via
    /// the X-API-User header so the entry is owned by them, not the API key's default admin.
    /// Returns true on success or "already there"; false on transient error.
    /// </summary>
    public async Task<bool> AddToWatchlistAsync(int tmdbId, int jellyseerrUserId, string mediaType = "movie")
    {
        var url = $"{_baseUrl}/api/v1/watchlist";
        var body = $"{{\"tmdbId\":{tmdbId},\"mediaType\":\"{mediaType}\"}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("X-API-User", jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return true;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)response.StatusCode == 409 ||
            responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already on", StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogWarning("Jellyseerr watchlist add failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return false;
    }

    /// <summary>
    /// Removes a TMDb title from the given Jellyseerr user's watchlist. Acts as that user
    /// via the X-API-User header. 404 is treated as success (the entry was already gone).
    /// </summary>
    public async Task<bool> RemoveFromWatchlistAsync(int tmdbId, int jellyseerrUserId, string mediaType = "movie")
    {
        var url = $"{_baseUrl}/api/v1/watchlist/{tmdbId}?mediaType={mediaType}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("X-API-User", jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return true;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        _logger.LogWarning("Jellyseerr watchlist remove failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return false;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) + "..." : s;

    public void Dispose() => _http.Dispose();
}
