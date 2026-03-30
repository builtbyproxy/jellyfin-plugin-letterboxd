using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdClient : IDisposable
{
    private static readonly Uri BaseUri = new Uri("https://api.letterboxd.com/");
    private static readonly string ClientId = GetAssemblyMetadata("LetterboxdClientId");
    private static readonly string ClientSecret = GetAssemblyMetadata("LetterboxdClientSecret");

    private static string GetAssemblyMetadata(string key)
    {
        var attributes = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
        foreach (System.Reflection.AssemblyMetadataAttribute attr in attributes)
        {
            if (attr.Key == key) return attr.Value ?? string.Empty;
        }
        return string.Empty;
    }

    private readonly ILogger _logger;
    private readonly HttpClient _client;
    public bool TokensRefreshed { get; private set; }

    private Account? _currentAccount;
    private string? _memberId;

    public LetterboxdClient(ILogger logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var retryHandler = new RetryDelegatingHandler(handler, _logger);

        _client = new HttpClient(retryHandler) { BaseAddress = BaseUri };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Letterboxd Android 3.5.0 (491) / Android 31 sdk_gphone64_x86_64");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }



    public async Task AuthenticateAsync(string username, string password)
    {
        // Shim for backward compatibility, creates a temporary account
        var tempAccount = new Account { LetterboxdUsername = username, LetterboxdPassword = password };
        await AuthenticateAsync(tempAccount).ConfigureAwait(false);
    }

    public async Task AuthenticateAsync(Account account)
    {
        _currentAccount = account;

        bool needsLogin = true;

        if (!string.IsNullOrEmpty(account.AccessToken) && !string.IsNullOrEmpty(account.RefreshToken))
        {
            if (account.TokenExpiration.HasValue && account.TokenExpiration.Value > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                // Token is still valid
                SetAuthorizationHeader(account.AccessToken);
                needsLogin = false;
            }
            else
            {
                // Try Refresh
                try
                {
                    _logger.LogInformation("Refreshing Letterboxd access token for {Username}", account.LetterboxdUsername);
                    var dict = new Dictionary<string, string>
                    {
                        { "grant_type", "refresh_token" },
                        { "refresh_token", account.RefreshToken },
                        { "client_id", ClientId },
                        { "client_secret", ClientSecret }
                    };

                    await PerformTokenRequestAsync(dict).ConfigureAwait(false);
                    needsLogin = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Token refresh failed: {Message}. Falling back to password login.", ex.Message);
                }
            }
        }

        if (needsLogin)
        {
            _logger.LogInformation("Logging into Letterboxd as {Username}", account.LetterboxdUsername);
            var dict = new Dictionary<string, string>
            {
                { "grant_type", "password" },
                { "username", account.LetterboxdUsername },
                { "password", account.LetterboxdPassword },
                { "client_id", ClientId },
                { "client_secret", ClientSecret }
            };

            await PerformTokenRequestAsync(dict).ConfigureAwait(false);
        }

        // Fetch MemberId if we don't know it yet
        try
        {
            var meJson = await _client.GetStringAsync("api/v0/me").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(meJson);
            if (doc.RootElement.TryGetProperty("member", out var memberEl) && memberEl.TryGetProperty("id", out var idEl))
            {
                _memberId = idEl.GetString();
                _logger.LogDebug("Resolved Member ID to {MemberId}", _memberId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to fetch member details during auth: {Message}", ex.Message);
        }
    }

    private void SetAuthorizationHeader(string token)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task PerformTokenRequestAsync(Dictionary<string, string> payload)
    {
        using var content = new FormUrlEncodedContent(payload);
        using var response = await _client.PostAsync("api/v0/auth/token", content).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Authentication failed ({response.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        if (_currentAccount != null && accessToken != null)
        {
            _currentAccount.AccessToken = accessToken;
            _currentAccount.TokenExpiration = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            if (refreshToken != null)
                _currentAccount.RefreshToken = refreshToken;

            TokensRefreshed = true;
            SetAuthorizationHeader(accessToken);
        }
        else
        {
            throw new Exception("Received invalid token payload.");
        }
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
    {
        var url = $"api/v0/search?input=tmdb:{tmdbId}&include=FilmSearchItem&perPage=10";
        var json = await _client.GetStringAsync(url).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
        {
            var firstItem = items[0];
            if (firstItem.TryGetProperty("film", out var filmEl) && filmEl.TryGetProperty("id", out var filmIdEl))
            {
                var filmId = filmIdEl.GetString()!;
                var filmSlug = filmEl.TryGetProperty("link", out var linkEl) ? linkEl.GetString()?.TrimEnd('/').Split('/').LastOrDefault() : null;
                return new FilmResult(filmSlug ?? string.Empty, filmId, filmId); // using filmId as productionId
            }
        }

        throw new Exception($"Could not find film matching TMDb ID {tmdbId}");
    }

    public async Task<DiaryInfo> GetDiaryInfoAsync(string filmId)
    {
        // To be safe, if a consumer passes a slug by accident, we just log it. We expect filmId here.
        var url = $"api/v0/film/{filmId}";
        try
        {
            var json = await _client.GetStringAsync(url).ConfigureAwait(false);
            return ParseDiaryInfo(json, filmId, _logger);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new DiaryInfo();
        }
    }

    public static DiaryInfo ParseDiaryInfo(string json, string filmId, ILogger logger)
    {
        using var doc = JsonDocument.Parse(json);
        var diaryInfo = new DiaryInfo();

        if (doc.RootElement.TryGetProperty("relationships", out var rels))
        {
            logger.LogInformation("Diary relationships found for {FilmId}: {Json}", filmId, rels.ToString());
            foreach (var rel in rels.EnumerateArray())
            {
                if (rel.TryGetProperty("relationship", out var r))
                {
                    var isWatched = r.TryGetProperty("watched", out var watchedEl) && watchedEl.GetBoolean();

                    if (isWatched)
                    {
                        diaryInfo.IsWatched = true;
                        if (r.TryGetProperty("whenWatched", out var whenWatchedEl))
                        {
                            if (DateTime.TryParse(whenWatchedEl.GetString(), out var lw))
                                diaryInfo.LastDate = lw;
                        }

                        if (r.TryGetProperty("diaryEntries", out var entriesEl))
                        {
                            if (entriesEl.ValueKind == JsonValueKind.Array && entriesEl.GetArrayLength() > 0)
                            {
                                diaryInfo.HasAnyEntry = true;
                                diaryInfo.LatestEntryId = entriesEl[0].GetString();
                                logger.LogInformation("Found {Count} diary entries for {FilmId}, latest ID: {Id}", entriesEl.GetArrayLength(), filmId, diaryInfo.LatestEntryId);
                            }
                            else
                            {
                                logger.LogInformation("diaryEntries property found for {FilmId} but had 0 length or not an array", filmId);
                            }
                        }
                        else
                        {
                            logger.LogWarning("diaryEntries property was MISSING completely from relationship for {FilmId}!", filmId);
                        }
                    }
                    else
                    {
                        logger.LogInformation("Relationship marked as watched:false for {FilmId}", filmId);
                    }
                }
            }
        }
        else
        {
            logger.LogWarning("No 'relationships' property found in film response for {FilmId}. Request might have been unauthenticated.", filmId);
        }

        return diaryInfo;
    }

    public async Task<string?> MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked, string? productionId = null, bool rewatch = false, double? rating = null, string? reviewText = null, bool containsSpoilers = false)
    {
        // Determine the ID to log against
        var targetId = !string.IsNullOrEmpty(productionId) ? productionId : filmId;

        // Payload for logging an entry
        var payload = new Dictionary<string, object>
        {
            { "filmId", targetId },
            { "diaryDetails", new Dictionary<string, object>
                {
                    { "diaryDate", date?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd") },
                    { "rewatch", rewatch }
                }
            },
            { "tags", new string[0] },
            { "like", liked }
        };

        if (rating.HasValue)
        {
            payload["rating"] = rating.Value;
        }

        if (!string.IsNullOrEmpty(reviewText))
        {
            payload["review"] = new Dictionary<string, object>
            {
                { "text", reviewText },
                { "containsSpoilers", containsSpoilers }
            };
        }

        var jsonContent = JsonSerializer.Serialize(payload);
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("api/v0/log-entries", content).ConfigureAwait(false);
        var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to mark '{filmId}' as watched: {response.StatusCode} - {respJson}");
        }

        using var doc = JsonDocument.Parse(respJson);
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    public async Task PostReviewAsync(string filmId, string? reviewText, bool containsSpoilers = false, bool isRewatch = false, string? date = null, double? rating = null)
    {
        // Note: For patches, we need the diary entry ID. We must query the film to get its diaryEntries array.
        var diaryInfo = await GetDiaryInfoAsync(filmId).ConfigureAwait(false);
        var targetEntryId = diaryInfo.LatestEntryId;

        if (string.IsNullOrEmpty(targetEntryId))
        {
            // Fallback: If no existing entry is found, POST a new one with the review included directly!
            targetEntryId = await MarkAsWatchedAsync(filmId, filmId, string.IsNullOrEmpty(date) ? DateTime.UtcNow : DateTime.Parse(date), false, filmId, isRewatch, rating, reviewText, containsSpoilers).ConfigureAwait(false);

            if (string.IsNullOrEmpty(targetEntryId))
            {
                throw new Exception($"Could not resolve Diary Entry ID for Film {filmId} after attempting to create one.");
            }

            // We just created it with the review, so we're done here!
            return;
        }

        // If an entry exists, we PATCH it
        var payload = new Dictionary<string, object>();
        var diaryDetails = new Dictionary<string, object>
        {
            { "rewatch", isRewatch }
        };

        if (!string.IsNullOrEmpty(date))
        {
            diaryDetails["diaryDate"] = date;
        }
        payload["diaryDetails"] = diaryDetails;

        if (!string.IsNullOrEmpty(reviewText))
        {
            payload["review"] = new Dictionary<string, object>
            {
                { "text", reviewText },
                { "containsSpoilers", containsSpoilers }
            };
        }

        if (rating.HasValue)
        {
            payload["rating"] = rating.Value;
        }

        var jsonContent = JsonSerializer.Serialize(payload);
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"), $"api/v0/log-entry/{targetEntryId}")
        {
            Content = content
        };

        using var response = await _client.SendAsync(patchReq).ConfigureAwait(false);
        var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to post review for '{filmId}': {response.StatusCode} - {respJson}");
        }
    }

    public async Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
    {
        if (string.IsNullOrEmpty(_memberId))
            throw new Exception("MemberId is null, authentication incomplete.");

        var tmdbIds = new List<int>();
        string? nextCursor = null;

        do
        {
            var url = $"api/v0/member/{_memberId}/watchlist?sort=Added&perPage=100";
            if (!string.IsNullOrEmpty(nextCursor)) url += $"&cursor={Uri.EscapeDataString(nextCursor)}";

            var json = await _client.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("links", out var links))
                    {
                        foreach (var link in links.EnumerateArray())
                        {
                            if (link.TryGetProperty("type", out var type) && type.GetString() == "tmdb")
                            {
                                if (link.TryGetProperty("id", out var tmdbIdStr) && int.TryParse(tmdbIdStr.GetString(), out var tId))
                                {
                                    tmdbIds.Add(tId);
                                }
                            }
                        }
                    }
                }
            }

            nextCursor = doc.RootElement.TryGetProperty("next", out var cursorEl) && cursorEl.ValueKind == JsonValueKind.String
                            ? cursorEl.GetString()
                            : null;

            if (!string.IsNullOrEmpty(nextCursor))
                await Task.Delay(1000).ConfigureAwait(false);

        } while (!string.IsNullOrEmpty(nextCursor));

        return tmdbIds.Distinct().ToList();
    }

    public async Task<List<int>> GetDiaryTmdbIdsAsync(string username)
    {
        if (string.IsNullOrEmpty(_memberId))
            throw new Exception("MemberId is null, authentication incomplete.");

        var tmdbIds = new List<int>();
        string? nextCursor = null;

        do
        {
            var url = $"api/v0/log-entries?perPage=100&member={_memberId}&memberRelationship=Owner&where=HasDiaryDate";
            if (!string.IsNullOrEmpty(nextCursor)) url += $"&cursor={Uri.EscapeDataString(nextCursor)}";

            var json = await _client.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("film", out var filmEl) && filmEl.TryGetProperty("links", out var links))
                    {
                        foreach (var link in links.EnumerateArray())
                        {
                            if (link.TryGetProperty("type", out var type) && type.GetString() == "tmdb")
                            {
                                if (link.TryGetProperty("id", out var tmdbIdStr) && int.TryParse(tmdbIdStr.GetString(), out var tId))
                                {
                                    tmdbIds.Add(tId);
                                }
                            }
                        }
                    }
                }
            }

            nextCursor = doc.RootElement.TryGetProperty("next", out var cursorEl) && cursorEl.ValueKind == JsonValueKind.String
                            ? cursorEl.GetString()
                            : null;

            if (!string.IsNullOrEmpty(nextCursor))
                await Task.Delay(1000).ConfigureAwait(false);

        } while (!string.IsNullOrEmpty(nextCursor));

        return tmdbIds.Distinct().ToList();
    }

    public async Task SetFilmLikeAsync(string filmId, bool liked)
    {
        var payload = new { liked = liked };
        var jsonContent = JsonSerializer.Serialize(payload);
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"), $"api/v0/film/{filmId}/me")
        {
            Content = content
        };

        using var response = await _client.SendAsync(patchReq).ConfigureAwait(false);
        var respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to mark '{filmId}' as liked={liked}: {response.StatusCode} - {respJson}");
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}


public class DiaryInfo
{
    public bool IsWatched { get; set; }
    public DateTime? LastDate { get; set; }
    public bool HasAnyEntry { get; set; }
    public string? LatestEntryId { get; set; }
}

public class RetryDelegatingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly int _maxRetries = 1;
    private readonly TimeSpan _delay = TimeSpan.FromSeconds(3);

    public RetryDelegatingHandler(HttpMessageHandler innerHandler, ILogger logger)
        : base(innerHandler)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (int i = 0; i <= _maxRetries; i++)
        {
            try
            {
                var reqToUse = i == 0 ? request : await CloneRequestAsync(request).ConfigureAwait(false);
                var response = await base.SendAsync(reqToUse, cancellationToken).ConfigureAwait(false);

                // Retry on transient 5xx errors or 429
                if (i < _maxRetries && (!response.IsSuccessStatusCode && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)))
                {
                    _logger.LogWarning("Letterboxd API returned {StatusCode} for {Method} {RequestUri}. Retrying in {DelaySeconds}s...", response.StatusCode, request.Method, request.RequestUri, _delay.TotalSeconds);
                    await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (i < _maxRetries)
            {
                _logger.LogWarning("HttpRequestException for {Method} {RequestUri}. Retrying in {DelaySeconds}s... ({Message})", request.Method, request.RequestUri, _delay.TotalSeconds, ex.Message);
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (i < _maxRetries && !cancellationToken.IsCancellationRequested) // Timeout
            {
                _logger.LogWarning("TaskCanceledException (Timeout) for {Method} {RequestUri}. Retrying in {DelaySeconds}s... ({Message})", request.Method, request.RequestUri, _delay.TotalSeconds, ex.Message);
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
        }

        // 1-retry fallback failed, throw original exception or return failed response
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri)
        {
            Version = req.Version
        };

        if (req.Content != null)
        {
            var ms = new MemoryStream();
            await req.Content.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var header in req.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
