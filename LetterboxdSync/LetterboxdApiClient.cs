using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdApiClient : ILetterboxdService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string _username = string.Empty;
    private string _memberId = string.Empty;
    private string _accessToken = string.Empty;

    private static readonly ConcurrentDictionary<string, TokenInfo> TokenCache = new();

    public LetterboxdApiClient(ILogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LetterboxdSync/1.5");
    }

    public async Task AuthenticateAsync(string username, string password, string? rawCookies = null)
    {
        _username = username;

        // Check token cache first
        if (TokenCache.TryGetValue(username, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            _accessToken = cached.AccessToken;
            _memberId = cached.MemberId;
            _logger.LogDebug("Reusing cached API token for {Username}", username);
            return;
        }

        // Try refresh if we have a refresh token
        if (cached != null && !string.IsNullOrEmpty(cached.RefreshToken))
        {
            try
            {
                await RefreshTokenAsync(cached.RefreshToken).ConfigureAwait(false);
                _logger.LogDebug("Refreshed API token for {Username}", username);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Token refresh failed for {Username}, doing full auth: {Message}", username, ex.Message);
            }
        }

        // Full password auth
        var body = $"grant_type=password&username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}";
        var response = await SendSignedAsync(HttpMethod.Post, "/auth/token", body, "application/x-www-form-urlencoded")
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Letterboxd API auth failed ({response.StatusCode}): {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        ParseTokenResponse(json, username);

        // Fetch member ID
        await FetchMemberIdAsync().ConfigureAwait(false);

        _logger.LogInformation("Authenticated with Letterboxd API as {Username}", username);
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/films", queryParams: $"filmId=tmdb%3A{tmdbId}&perPage=1")
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        if (items.GetArrayLength() == 0)
            throw new Exception($"Film with TMDb ID {tmdbId} not found on Letterboxd");

        var film = items[0];
        var lid = film.GetProperty("id").GetString()!;
        var slug = ExtractSlugFromLink(film);

        return new FilmResult(slug, lid, null);
    }

    public async Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username)
    {
        EnsureAuthenticated();

        // filmIdOrSlug is the LID when coming from the API path
        var response = await SendSignedAsync(HttpMethod.Get, "/log-entries",
            queryParams: $"member={Uri.EscapeDataString(_memberId)}&film={Uri.EscapeDataString(filmIdOrSlug)}&perPage=1&sort=WhenAdded",
            authenticated: true).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new DiaryInfo(null, false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        if (items.GetArrayLength() == 0)
            return new DiaryInfo(null, false);

        var entry = items[0];
        DateTime? lastDate = null;
        if (entry.TryGetProperty("diaryDetails", out var details) &&
            details.TryGetProperty("diaryDate", out var dateStr))
        {
            if (DateTime.TryParse(dateStr.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                lastDate = parsed;
        }

        return new DiaryInfo(lastDate, true);
    }

    public async Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null)
    {
        EnsureAuthenticated();

        var viewingDate = date ?? DateTime.Now;
        var bodyObj = new Dictionary<string, object>
        {
            ["filmId"] = filmId,
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = viewingDate.ToString("yyyy-MM-dd"),
                ["rewatch"] = rewatch
            },
            ["like"] = liked,
            ["tags"] = Array.Empty<string>()
        };

        if (rating.HasValue)
            bodyObj["rating"] = rating.Value;

        var body = JsonSerializer.Serialize(bodyObj);

        var response = await SendSignedAsync(HttpMethod.Post, "/log-entries", body, "application/json", authenticated: true)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearCachedToken();
            throw new Exception("Letterboxd API token expired. Will re-authenticate on next sync.");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Failed to log film: {response.StatusCode} {errorBody}");
        }
    }

    public async Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null)
    {
        EnsureAuthenticated();

        // Need to look up the film LID from slug
        var filmResponse = await SendSignedAsync(HttpMethod.Get, "/films",
            queryParams: $"perPage=1").ConfigureAwait(false);

        // For reviews, we use the filmSlug to resolve the LID
        // The caller should have already done a lookup, so filmSlug might be a LID
        // Try direct film endpoint first
        var lookupResponse = await SendSignedAsync(HttpMethod.Get, $"/film/{Uri.EscapeDataString(filmSlug)}")
            .ConfigureAwait(false);

        string filmId;
        if (lookupResponse.IsSuccessStatusCode)
        {
            var lookupJson = await lookupResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var lookupDoc = JsonDocument.Parse(lookupJson);
            filmId = lookupDoc.RootElement.GetProperty("id").GetString()!;
        }
        else
        {
            // filmSlug might already be a LID
            filmId = filmSlug;
        }

        var bodyObj = new Dictionary<string, object>
        {
            ["filmId"] = filmId,
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = date ?? DateTime.Now.ToString("yyyy-MM-dd"),
                ["rewatch"] = isRewatch
            },
            ["like"] = false,
            ["tags"] = Array.Empty<string>()
        };

        if (!string.IsNullOrWhiteSpace(reviewText))
        {
            bodyObj["review"] = new Dictionary<string, object>
            {
                ["text"] = reviewText,
                ["containsSpoilers"] = containsSpoilers
            };
        }

        if (rating.HasValue)
            bodyObj["rating"] = rating.Value;

        var body = JsonSerializer.Serialize(bodyObj);

        var response = await SendSignedAsync(HttpMethod.Post, "/log-entries", body, "application/json", authenticated: true)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Failed to post review: {response.StatusCode} {errorBody}");
        }
    }

    public async Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
    {
        EnsureAuthenticated();
        var tmdbIds = new List<int>();
        string? cursor = null;

        for (int page = 0; page < 50; page++)
        {
            var qp = $"perPage=100&member={Uri.EscapeDataString(_memberId)}";
            if (cursor != null)
                qp += $"&cursor={Uri.EscapeDataString(cursor)}";

            var response = await SendSignedAsync(HttpMethod.Get, $"/member/{Uri.EscapeDataString(_memberId)}/watchlist",
                queryParams: qp, authenticated: true).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");

            if (items.GetArrayLength() == 0)
                break;

            foreach (var item in items.EnumerateArray())
            {
                var tmdbId = ExtractTmdbId(item);
                if (tmdbId.HasValue)
                    tmdbIds.Add(tmdbId.Value);
            }

            if (doc.RootElement.TryGetProperty("cursor", out var cursorEl))
                cursor = cursorEl.GetString();
            else
                break;
        }

        return tmdbIds;
    }

    public async Task<List<int>> GetDiaryTmdbIdsAsync(string username)
    {
        EnsureAuthenticated();
        var tmdbIds = new List<int>();
        string? cursor = null;

        for (int page = 0; page < 50; page++)
        {
            var qp = $"perPage=100&member={Uri.EscapeDataString(_memberId)}";
            if (cursor != null)
                qp += $"&cursor={Uri.EscapeDataString(cursor)}";

            var response = await SendSignedAsync(HttpMethod.Get, "/log-entries",
                queryParams: qp, authenticated: true).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");

            if (items.GetArrayLength() == 0)
                break;

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("film", out var film))
                {
                    var tmdbId = ExtractTmdbId(film);
                    if (tmdbId.HasValue && !tmdbIds.Contains(tmdbId.Value))
                        tmdbIds.Add(tmdbId.Value);
                }
            }

            if (doc.RootElement.TryGetProperty("cursor", out var cursorEl))
                cursor = cursorEl.GetString();
            else
                break;
        }

        return tmdbIds;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // --- Private helpers ---

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path,
        string? body = null, string? contentType = null, string? queryParams = null, bool authenticated = false)
    {
        var nonce = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var url = $"{LetterboxdApiConstants.BaseUrl}{path}?apikey={LetterboxdApiConstants.ApiKey}&nonce={nonce}&timestamp={timestamp}";
        if (!string.IsNullOrEmpty(queryParams))
            url = $"{LetterboxdApiConstants.BaseUrl}{path}?{queryParams}&apikey={LetterboxdApiConstants.ApiKey}&nonce={nonce}&timestamp={timestamp}";

        var bodyStr = body ?? string.Empty;
        var sigInput = $"{method.Method}\0{url}\0{bodyStr}";
        var signature = ComputeHmacSha256(LetterboxdApiConstants.ApiSecret, sigInput);

        url += $"&signature={signature}";

        using var request = new HttpRequestMessage(method, url);

        if (authenticated && !string.IsNullOrEmpty(_accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        if (body != null && contentType != null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

        var response = await _http.SendAsync(request).ConfigureAwait(false);

        // Handle rate limiting
        if (response.StatusCode == (HttpStatusCode)429)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10;
            _logger.LogWarning("Letterboxd API rate limited, waiting {Seconds}s", retryAfter);
            await Task.Delay(TimeSpan.FromSeconds(retryAfter)).ConfigureAwait(false);

            // Retry once
            using var retryRequest = new HttpRequestMessage(method, url);
            if (authenticated && !string.IsNullOrEmpty(_accessToken))
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            if (body != null && contentType != null)
                retryRequest.Content = new StringContent(body, Encoding.UTF8, contentType);
            response = await _http.SendAsync(retryRequest).ConfigureAwait(false);
        }

        return response;
    }

    private static string ComputeHmacSha256(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    private void ParseTokenResponse(string json, string username)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        TokenCache[username] = new TokenInfo(
            _accessToken,
            refreshToken,
            DateTime.UtcNow.AddSeconds(expiresIn),
            _memberId);
    }

    private async Task RefreshTokenAsync(string refreshToken)
    {
        var body = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}";
        var response = await SendSignedAsync(HttpMethod.Post, "/auth/token", body, "application/x-www-form-urlencoded")
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        ParseTokenResponse(json, _username);
        await FetchMemberIdAsync().ConfigureAwait(false);
    }

    private async Task FetchMemberIdAsync()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/me", authenticated: true).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        _memberId = doc.RootElement.GetProperty("member").GetProperty("id").GetString()!;

        // Update cache with member ID
        if (TokenCache.TryGetValue(_username, out var cached))
        {
            TokenCache[_username] = cached with { MemberId = _memberId };
        }
    }

    private void ClearCachedToken()
    {
        TokenCache.TryRemove(_username, out _);
        _accessToken = string.Empty;
        _logger.LogWarning("API token expired, cleared cache");
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
    }

    private static string ExtractSlugFromLink(JsonElement film)
    {
        if (film.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("type", out var type) && type.GetString() == "letterboxd" &&
                    link.TryGetProperty("url", out var url))
                {
                    var urlStr = url.GetString() ?? string.Empty;
                    // https://letterboxd.com/film/fight-club/ -> fight-club
                    var parts = urlStr.TrimEnd('/').Split('/');
                    return parts.Length > 0 ? parts[^1] : string.Empty;
                }
            }
        }

        // Fallback: extract from top-level link property
        if (film.TryGetProperty("link", out var directLink))
        {
            var urlStr = directLink.GetString() ?? string.Empty;
            var parts = urlStr.TrimEnd('/').Split('/');
            return parts.Length > 0 ? parts[^1] : string.Empty;
        }

        return string.Empty;
    }

    private static int? ExtractTmdbId(JsonElement film)
    {
        if (film.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("type", out var type) && type.GetString() == "tmdb" &&
                    link.TryGetProperty("id", out var id))
                {
                    if (int.TryParse(id.GetString(), out var tmdbId))
                        return tmdbId;
                }
            }
        }

        return null;
    }

    internal record TokenInfo(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, string MemberId);
}
