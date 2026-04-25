using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Lightweight Jellyseerr client for fetching the Jellyfin-user-to-Jellyseerr-user
/// mapping and creating per-user movie requests by TMDb ID.
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
    /// Creates a movie request in Jellyseerr for the given TMDb ID, attributed to the given Jellyseerr user.
    /// Returns true on success or "already requested"; false on transient error.
    /// </summary>
    public async Task<bool> RequestMovieAsync(int tmdbId, int jellyseerrUserId)
    {
        var url = $"{_baseUrl}/api/v1/request";
        var body = $"{{\"mediaType\":\"movie\",\"mediaId\":{tmdbId},\"userId\":{jellyseerrUserId}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return true;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        // Jellyseerr returns 409 when already requested; treat as a success (no-op).
        if ((int)response.StatusCode == 409 ||
            responseBody.Contains("REQUEST_EXISTS", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already requested", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already available", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Jellyseerr already has request for TMDb {TmdbId} (user {UserId}): {Body}",
                tmdbId, jellyseerrUserId, Truncate(responseBody, 200));
            return true;
        }

        _logger.LogWarning("Jellyseerr request failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return false;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) + "..." : s;

    public void Dispose() => _http.Dispose();
}
