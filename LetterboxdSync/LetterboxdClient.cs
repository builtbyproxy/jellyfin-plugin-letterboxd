using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdClient : IDisposable
{
    private static readonly Uri BaseUri = new Uri("https://letterboxd.com/");
    private const int MaxRetries = 3;

    private readonly ILogger _logger;
    private readonly CookieContainer _cookieContainer = new();
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _client;
    private string _csrf = string.Empty;
    private string _username = string.Empty;

    public LetterboxdClient(ILogger logger)
    {
        _logger = logger;
        _handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        _client = new HttpClient(_handler) { BaseAddress = BaseUri };

        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:134.0) Gecko/20100101 Firefox/134.0");
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        _client.DefaultRequestHeaders.Connection.Add("keep-alive");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "none");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-user", "?1");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("upgrade-insecure-requests", "1");
    }

    public void SetRawCookies(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var part in raw.Split(';'))
        {
            var kv = part.Trim();
            if (string.IsNullOrEmpty(kv)) continue;
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;

            var name = kv.Substring(0, eq).Trim();
            var val = WebUtility.UrlDecode(kv.Substring(eq + 1).Trim());

            try
            {
                _cookieContainer.Add(BaseUri, new Cookie(name, val, "/", "letterboxd.com"));
                _cookieContainer.Add(BaseUri, new Cookie(name, val, "/", ".letterboxd.com"));

                if (string.Equals(name, "com.xk72.webparts.csrf", StringComparison.OrdinalIgnoreCase))
                    _csrf = val;
            }
            catch { }
        }
    }

    private bool HasAuthenticatedSession()
    {
        var cookies = _cookieContainer.GetCookies(BaseUri);
        return !string.IsNullOrWhiteSpace(cookies["letterboxd.user.CURRENT"]?.Value);
    }

    private string GetCsrfFromCookie()
    {
        var cookies = _cookieContainer.GetCookies(BaseUri);
        return cookies["com.xk72.webparts.csrf"]?.Value ?? string.Empty;
    }

    private async Task RefreshCsrfAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        SetNavHeaders(request.Headers);
        using var response = await _client.SendAsync(request).ConfigureAwait(false);

        var token = GetCsrfFromCookie();
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Could not read CSRF cookie after refresh.");
        _csrf = token;
    }

    public async Task AuthenticateAsync(string username, string password)
    {
        _username = username;

        if (HasAuthenticatedSession())
        {
            try
            {
                await RefreshCsrfAsync().ConfigureAwait(false);
                _logger.LogInformation("Reusing existing session for {Username}", username);
                return;
            }
            catch
            {
                _logger.LogWarning("Existing session expired for {Username}, performing fresh login", username);
            }
        }

        await Task.Delay(500 + Random.Shared.Next(1000)).ConfigureAwait(false);

        // GET /sign-in/ for CSRF token
        string signInCsrf;
        using (var signInReq = new HttpRequestMessage(HttpMethod.Get, "/sign-in/"))
        {
            SetNavHeaders(signInReq.Headers);
            using var signInRes = await _client.SendAsync(signInReq).ConfigureAwait(false);

            if (signInRes.StatusCode == HttpStatusCode.Forbidden)
            {
                // Try warming up with cf_clearance cookie
                if (_cookieContainer.GetCookies(BaseUri).Cast<Cookie>()
                    .Any(c => c.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase)))
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    using var warmup = new HttpRequestMessage(HttpMethod.Get, "/");
                    SetNavHeaders(warmup.Headers);
                    using var _ = await _client.SendAsync(warmup).ConfigureAwait(false);

                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, "/sign-in/");
                    SetNavHeaders(retryReq.Headers);
                    using var retryRes = await _client.SendAsync(retryReq).ConfigureAwait(false);

                    if (retryRes.StatusCode == HttpStatusCode.Forbidden)
                        throw new Exception("Letterboxd returned 403 even with Cloudflare cookies. Try refreshing your raw cookies.");

                    var retryHtml = await retryRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                    signInCsrf = ExtractHiddenInput(retryHtml, "__csrf")
                        ?? throw new Exception("Could not find __csrf on /sign-in/ after Cloudflare retry.");
                    _csrf = signInCsrf;
                    goto DoLogin;
                }

                throw new Exception("Letterboxd returned 403 on /sign-in/. Likely Cloudflare. Provide raw cookies with cf_clearance.");
            }

            signInRes.EnsureSuccessStatusCode();
            var html = await signInRes.Content.ReadAsStringAsync().ConfigureAwait(false);
            signInCsrf = ExtractHiddenInput(html, "__csrf")
                ?? throw new Exception("Could not find __csrf on /sign-in/.");
            _csrf = signInCsrf;
        }

        DoLogin:
        await Task.Delay(3000 + Random.Shared.Next(4000)).ConfigureAwait(false);

        using (var loginReq = new HttpRequestMessage(HttpMethod.Post, "/user/login.do"))
        {
            loginReq.Headers.Accept.Clear();
            loginReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            loginReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/javascript"));
            loginReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.01));
            loginReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            loginReq.Headers.Referrer = new Uri("https://letterboxd.com/sign-in/");
            loginReq.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
            loginReq.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            loginReq.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            loginReq.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");

            loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "__csrf", _csrf },
                { "remember", "true" },
                { "authenticationCode", "" }
            });

            using var loginRes = await _client.SendAsync(loginReq).ConfigureAwait(false);

            if (loginRes.StatusCode == HttpStatusCode.Forbidden)
                throw new Exception("Letterboxd returned 403 during login. Likely reCAPTCHA. Provide raw cookies instead.");

            loginRes.EnsureSuccessStatusCode();

            var body = await loginRes.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var json = doc.RootElement;

            if (json.TryGetProperty("result", out var result) && result.GetString() == "error")
            {
                var msg = "Login failed";
                if (json.TryGetProperty("messages", out var msgs))
                {
                    var sb = new StringBuilder();
                    foreach (var m in msgs.EnumerateArray())
                        sb.Append(m.GetString()).Append(' ');
                    msg = sb.ToString().Trim();
                }
                throw new Exception($"Letterboxd login error: {msg}");
            }
        }

        await RefreshCsrfAsync().ConfigureAwait(false);
        _logger.LogInformation("Authenticated with Letterboxd as {Username}", username);
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
    {
        await Task.Delay(3000 + Random.Shared.Next(2000)).ConfigureAwait(false);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/tmdb/{tmdbId}");
        SetNavHeaders(req.Headers, "same-origin");
        using var res = await _client.SendAsync(req).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new Exception($"TMDb lookup returned 403 for /tmdb/{tmdbId}. Cloudflare is likely blocking. Try providing raw cookies.");

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new Exception($"Film with TMDb ID {tmdbId} not found on Letterboxd.");

        res.EnsureSuccessStatusCode();

        var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        // Extract film URL from canonical link or /film/ anchor
        var filmUrl = htmlDoc.DocumentNode
            .SelectSingleNode("//link[@rel='canonical']")
            ?.GetAttributeValue("href", string.Empty) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(filmUrl))
        {
            var anchor = htmlDoc.DocumentNode.SelectSingleNode("//a[starts-with(@href, '/film/')]");
            var href = anchor?.GetAttributeValue("href", string.Empty) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(href))
                filmUrl = href.StartsWith("/") ? "https://letterboxd.com" + href : href;
        }

        if (string.IsNullOrWhiteSpace(filmUrl))
            throw new Exception($"Could not resolve film URL from TMDb ID {tmdbId}.");

        var filmUri = new Uri(filmUrl, UriKind.Absolute);
        var segments = filmUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 || !segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"TMDb page resolved to non-film URL: '{filmUrl}'");

        var filmSlug = segments[1];

        // Load film page to get the internal filmId and productionId
        using var filmReq = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/");
        SetNavHeaders(filmReq.Headers, "same-origin", $"https://letterboxd.com/tmdb/{tmdbId}");
        using var filmRes = await _client.SendAsync(filmReq).ConfigureAwait(false);
        filmRes.EnsureSuccessStatusCode();

        // Try to get productionId from response header
        string? productionId = null;
        if (filmRes.Headers.TryGetValues("x-letterboxd-identifier", out var headerValues))
            productionId = headerValues.FirstOrDefault();

        var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        var filmDoc = new HtmlDocument();
        filmDoc.LoadHtml(filmHtml);

        var el = filmDoc.DocumentNode.SelectSingleNode($"//div[@data-film-slug='{filmSlug}']")
              ?? filmDoc.DocumentNode.SelectSingleNode($"//div[@data-item-link='/film/{filmSlug}/']")
              ?? filmDoc.DocumentNode.SelectSingleNode("//div[@data-film-id]");

        if (el == null)
            throw new Exception($"Could not find film element on page /film/{filmSlug}/");

        var filmId = el.GetAttributeValue("data-film-id", string.Empty);
        if (string.IsNullOrEmpty(filmId))
            throw new Exception($"data-film-id attribute empty on /film/{filmSlug}/");

        // Fallback: try to get productionId from data-postered-identifier attribute
        if (string.IsNullOrEmpty(productionId))
        {
            var posterEl = filmDoc.DocumentNode.SelectSingleNode("//div[@data-postered-identifier]");
            var posterJson = posterEl?.GetAttributeValue("data-postered-identifier", string.Empty);
            if (!string.IsNullOrEmpty(posterJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(posterJson);
                    if (doc.RootElement.TryGetProperty("lid", out var lid))
                        productionId = lid.GetString();
                }
                catch { }
            }
        }

        _logger.LogInformation("Resolved TMDb:{TmdbId} -> slug={Slug}, filmId={FilmId}, productionId={ProductionId}",
            tmdbId, filmSlug, filmId, productionId ?? "null");
        return new FilmResult(filmSlug, filmId, productionId);
    }

    public async Task<DateTime?> GetLastDiaryDateAsync(string filmSlug)
    {
        var url = $"/{_username}/film/{filmSlug}/diary/";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetNavHeaders(req.Headers, "same-origin");
        using var res = await _client.SendAsync(req).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
            return null;

        var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var months = doc.DocumentNode.SelectNodes("//a[contains(@class, 'month')]");
        var days = doc.DocumentNode.SelectNodes("//a[contains(@class, 'date') or contains(@class, 'daydate')]");
        var years = doc.DocumentNode.SelectNodes("//a[contains(@class, 'year')]");

        if (months == null || days == null || years == null)
            return null;

        var dates = new List<DateTime>();
        var count = Math.Min(Math.Min(months.Count, days.Count), years.Count);

        for (int i = 0; i < count; i++)
        {
            var dateStr = $"{days[i].InnerText?.Trim()} {months[i].InnerText?.Trim()} {years[i].InnerText?.Trim()}";
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                dates.Add(parsed);
        }

        return dates.Count > 0 ? dates.Max() : null;
    }

    public async Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked, string? productionId = null)
    {
        var viewingDate = date ?? DateTime.Now;
        _logger.LogDebug("MarkAsWatched: slug={Slug}, productionId={ProductionId}, date={Date}",
            filmSlug, productionId ?? "null", viewingDate.ToString("yyyy-MM-dd"));

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            await RefreshCsrfAsync().ConfigureAwait(false);

            // Try the new JSON API first (production-log-entries), fall back to log-entries
            var endpoints = new[] { "/api/v0/production-log-entries", "/api/v0/log-entries" };

            foreach (var endpoint in endpoints)
            {
                string jsonBody;
                if (endpoint.Contains("production") && !string.IsNullOrEmpty(productionId))
                {
                    jsonBody = JsonSerializer.Serialize(new
                    {
                        productionId = productionId,
                        diaryDetails = new
                        {
                            diaryDate = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            rewatch = false
                        },
                        tags = Array.Empty<string>(),
                        like = liked
                    });
                }
                else
                {
                    jsonBody = JsonSerializer.Serialize(new
                    {
                        filmId = filmId,
                        diaryDetails = new
                        {
                            diaryDate = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            rewatch = false
                        },
                        tags = Array.Empty<string>(),
                        like = liked
                    });
                }

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.Referrer = new Uri($"https://letterboxd.com/film/{filmSlug}/");
                req.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _csrf);
                req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
                req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
                req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var res = await _client.SendAsync(req).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                _logger.LogInformation("MarkAsWatched attempt {Attempt} ({Endpoint}): status={Status}, bodyLen={Len}",
                    attempt + 1, endpoint, (int)res.StatusCode, body.Length);

                // If this endpoint returns 404, try the next one
                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Endpoint {Endpoint} returned 404, trying next", endpoint);
                    continue;
                }

                if (res.StatusCode == HttpStatusCode.Forbidden)
                    throw new Exception($"Letterboxd returned 403 for {filmSlug}. Likely anti-bot.");

                if ((int)res.StatusCode >= 200 && (int)res.StatusCode < 300)
                {
                    _logger.LogInformation("Successfully logged {Slug} via {Endpoint}", filmSlug, endpoint);
                    return;
                }

                // Non-404 error — log and check for transient
                var sanitized = Regex.Replace(body, @"\s+", " ").Trim();
                _logger.LogError("Diary save failed for {Slug} via {Endpoint}: status={Status}, body={Body}",
                    filmSlug, endpoint, (int)res.StatusCode, Truncate(sanitized, 500));

                if (attempt < MaxRetries - 1 &&
                    (sanitized.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                     sanitized.Contains("try again", StringComparison.OrdinalIgnoreCase)))
                {
                    var delayMs = (attempt + 1) * 5000 + Random.Shared.Next(3000);
                    _logger.LogWarning("Transient error, retrying in {Delay}ms", delayMs);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    goto NextAttempt;
                }

                throw new Exception($"Letterboxd returned {(int)res.StatusCode} for {filmSlug}");
            }

            // Both endpoints returned 404
            if (attempt < MaxRetries - 1)
            {
                var delayMs = (attempt + 1) * 5000 + Random.Shared.Next(3000);
                _logger.LogWarning("All endpoints returned 404, retrying in {Delay}ms", delayMs);
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            NextAttempt:;
        }

        throw new Exception($"Failed to log {filmSlug} after {MaxRetries} retries.");
    }

    private static bool IsSuccess(JsonElement json, out string message)
    {
        message = string.Empty;

        if (json.TryGetProperty("messages", out var msgs))
        {
            var sb = new StringBuilder();
            foreach (var m in msgs.EnumerateArray())
                sb.Append(m.GetString());
            message = sb.ToString();
        }

        if (json.TryGetProperty("result", out var result))
        {
            return result.ValueKind switch
            {
                JsonValueKind.String => result.GetString() != "error",
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };
        }

        return false;
    }

    private void SetNavHeaders(HttpRequestHeaders headers, string site = "none", string? referrer = null)
    {
        headers.TryAddWithoutValidation("sec-fetch-dest", "document");
        headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        headers.TryAddWithoutValidation("sec-fetch-site", site);
        headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

        if (referrer != null)
            headers.Referrer = new Uri(referrer);
    }

    private static string? ExtractHiddenInput(string html, string name)
    {
        var pattern = $@"<input[^>]*\bname\s*=\s*[""{Regex.Escape(name)}""][^>]*\bvalue\s*=\s*[""']([^""']*)[""'][^>]*>";
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string Truncate(string s, int max = 300)
        => s.Length > max ? s.Substring(0, max) : s;

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}
