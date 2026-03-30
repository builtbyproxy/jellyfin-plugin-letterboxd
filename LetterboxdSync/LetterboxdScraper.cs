using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Scrapes Letterboxd HTML pages: film lookup, diary info, watchlist, diary TMDb IDs.
/// </summary>
public class LetterboxdScraper
{
    private readonly LetterboxdHttpClient _http;
    private readonly ILogger _logger;

    public LetterboxdScraper(LetterboxdHttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
    {
        await Task.Delay(3000 + Random.Shared.Next(2000)).ConfigureAwait(false);

        using var res = await _http.GetWithCloudflareRetryAsync($"/tmdb/{tmdbId}").ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new Exception($"TMDb lookup returned 403 for /tmdb/{tmdbId} after retries. Cloudflare is blocking. Try providing raw cookies.");

        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new Exception($"Film with TMDb ID {tmdbId} not found on Letterboxd.");

        res.EnsureSuccessStatusCode();

        var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        var filmSlug = ExtractFilmSlugFromHtml(html, tmdbId);

        // Load film page to get the internal filmId and productionId
        using var filmReq = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/");
        _http.SetNavHeaders(filmReq.Headers, "same-origin", $"https://letterboxd.com/tmdb/{tmdbId}");
        using var filmRes = await _http.Http.SendAsync(filmReq).ConfigureAwait(false);
        filmRes.EnsureSuccessStatusCode();

        var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        var (filmId, productionId) = ExtractFilmIdentifiers(filmHtml, filmSlug, filmRes.Headers);

        _logger.LogInformation("Resolved TMDb:{TmdbId} -> slug={Slug}, filmId={FilmId}, productionId={ProductionId}",
            tmdbId, filmSlug, filmId, productionId ?? "null");
        return new FilmResult(filmSlug, filmId, productionId);
    }

    public async Task<DiaryInfo> GetDiaryInfoAsync(string filmSlug, string username)
    {
        var url = $"/{username}/film/{filmSlug}/diary/";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        _http.SetNavHeaders(req.Headers, "same-origin");
        using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
            return new DiaryInfo(null, false);

        var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        var dates = Helpers.ParseDiaryDates(html);

        return new DiaryInfo(
            dates.Count > 0 ? dates.Max() : null,
            dates.Count > 0
        );
    }

    public async Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
    {
        var tmdbIds = new List<int>();
        var page = 1;
        const int maxPages = 50;

        while (page <= maxPages)
        {
            await Task.Delay(2000 + Random.Shared.Next(2000)).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{username}/watchlist/page/{page}/");
            _http.SetNavHeaders(req.Headers, "same-origin");
            using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Watchlist page {Page} for {Username} returned {Status}. Returning {Count} films found so far.",
                    page, username, (int)res.StatusCode, tmdbIds.Count);
                break;
            }

            var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Cloudflare challenge detected on watchlist page {Page} for {Username}. " +
                    "Returning {Count} films found so far. Try providing raw cookies with cf_clearance.",
                    page, username, tmdbIds.Count);
                break;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var posters = doc.DocumentNode.SelectNodes("//div[@data-component-class='LazyPoster']");
            if (posters == null || posters.Count == 0) break;

            foreach (var poster in posters)
            {
                var slug = poster.GetAttributeValue("data-item-slug", string.Empty);
                if (string.IsNullOrEmpty(slug)) continue;

                var tmdbId = await ResolveTmdbIdFromSlugAsync(slug).ConfigureAwait(false);
                if (tmdbId.HasValue)
                {
                    tmdbIds.Add(tmdbId.Value);
                    _logger.LogDebug("Watchlist: {Slug} -> TMDb:{TmdbId}", slug, tmdbId.Value);
                }
            }

            var nextPage = doc.DocumentNode.SelectSingleNode($"//li[a/text() = '{page + 1}']");
            if (nextPage == null) break;
            page++;
        }

        return tmdbIds;
    }

    /// <summary>
    /// Scrape all films a user has logged on Letterboxd and return their TMDb IDs.
    /// Uses /{username}/films/ (all watched films) rather than /films/diary/
    /// (which only shows films with dated diary entries).
    /// </summary>
    public async Task<List<int>> GetDiaryTmdbIdsAsync(string username)
    {
        var tmdbIds = new List<int>();
        var page = 1;
        const int maxPages = 50;

        while (page <= maxPages)
        {
            await Task.Delay(2000 + Random.Shared.Next(2000)).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{username}/films/page/{page}/");
            _http.SetNavHeaders(req.Headers, "same-origin");
            using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Films page {Page} for {Username} returned {Status}. Returning {Count} films found so far.",
                    page, username, (int)res.StatusCode, tmdbIds.Count);
                break;
            }

            var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Detect Cloudflare challenge page (200 with "Just a moment..." content)
            if (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Cloudflare challenge detected on films page {Page} for {Username}. " +
                    "Returning {Count} films found so far. Try providing raw cookies with cf_clearance.",
                    page, username, tmdbIds.Count);
                break;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var posters = doc.DocumentNode.SelectNodes("//li[contains(@class, 'poster-container')]//div[@data-film-slug]");
            if (posters == null)
                posters = doc.DocumentNode.SelectNodes("//div[@data-component-class='LazyPoster']");
            if (posters == null)
                posters = doc.DocumentNode.SelectNodes("//div[@data-film-slug]");

            if (posters == null || posters.Count == 0)
            {
                if (page == 1)
                    _logger.LogWarning("No films found on page 1 for {Username}. " +
                        "Profile may be private or HTML structure may have changed.", username);
                break;
            }

            _logger.LogInformation("Films page {Page} for {Username}: found {Count} posters, resolving TMDb IDs...",
                page, username, posters.Count);
            SyncProgress.SetPhase($"Scanning page {page}");

            foreach (var poster in posters)
            {
                var slug = poster.GetAttributeValue("data-film-slug", string.Empty);
                if (string.IsNullOrEmpty(slug))
                    slug = poster.GetAttributeValue("data-item-slug", string.Empty);
                if (string.IsNullOrEmpty(slug)) continue;

                var tmdbId = await ResolveTmdbIdFromSlugAsync(slug).ConfigureAwait(false);
                if (tmdbId.HasValue && !tmdbIds.Contains(tmdbId.Value))
                {
                    tmdbIds.Add(tmdbId.Value);
                    _logger.LogDebug("Films: {Slug} -> TMDb:{TmdbId}", slug, tmdbId.Value);
                }
            }

            var nextPage = doc.DocumentNode.SelectSingleNode($"//li[a/text() = '{page + 1}']");
            if (nextPage == null) break;
            page++;
        }

        _logger.LogInformation("Found {Count} films across {Pages} pages for {Username}",
            tmdbIds.Count, page, username);

        return tmdbIds;
    }

    /// <summary>
    /// Extract productionId and filmId from a film page's HTML and response headers.
    /// Shared by LookupFilmByTmdbIdAsync and PostReviewAsync.
    /// </summary>
    internal (string FilmId, string? ProductionId) ExtractFilmIdentifiers(
        string filmHtml, string filmSlug, System.Net.Http.Headers.HttpResponseHeaders? responseHeaders = null)
    {
        string? productionId = null;
        if (responseHeaders?.TryGetValues("x-letterboxd-identifier", out var headerValues) == true)
        {
            using var enumerator = headerValues.GetEnumerator();
            if (enumerator.MoveNext())
                productionId = enumerator.Current;
        }

        var filmDoc = new HtmlDocument();
        filmDoc.LoadHtml(filmHtml);

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

        var el = filmDoc.DocumentNode.SelectSingleNode($"//div[@data-film-slug='{filmSlug}']")
              ?? filmDoc.DocumentNode.SelectSingleNode($"//div[@data-item-link='/film/{filmSlug}/']")
              ?? filmDoc.DocumentNode.SelectSingleNode("//div[@data-film-id]");

        if (el == null)
            throw new Exception($"Could not find film element on page /film/{filmSlug}/");

        var filmId = el.GetAttributeValue("data-film-id", string.Empty);
        if (string.IsNullOrEmpty(filmId))
            throw new Exception($"data-film-id attribute empty on /film/{filmSlug}/");

        return (filmId, productionId);
    }

    private string ExtractFilmSlugFromHtml(string html, int tmdbId)
    {
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

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

        return segments[1];
    }

    private async Task<int?> ResolveTmdbIdFromSlugAsync(string slug)
    {
        // Check cache first — avoids HTTP request for previously resolved slugs
        var cached = TmdbCache.Get(slug);
        if (cached.HasValue)
        {
            SyncProgress.IncrementCacheHit();
            SyncProgress.IncrementProcessed();
            return cached.Value;
        }

        SyncProgress.IncrementNewLookup();

        await Task.Delay(2000 + Random.Shared.Next(1000)).ConfigureAwait(false);

        using var filmReq = new HttpRequestMessage(HttpMethod.Get, $"/film/{slug}/");
        _http.SetNavHeaders(filmReq.Headers);
        using var filmRes = await _http.Http.SendAsync(filmReq).ConfigureAwait(false);
        if (!filmRes.IsSuccessStatusCode) return null;

        var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        var filmDoc = new HtmlDocument();
        filmDoc.LoadHtml(filmHtml);

        var body = filmDoc.DocumentNode.SelectSingleNode("//body");
        var tmdbStr = body?.GetAttributeValue("data-tmdb-id", string.Empty);
        if (!string.IsNullOrEmpty(tmdbStr) && int.TryParse(tmdbStr, out var id))
        {
            TmdbCache.Set(slug, id);
            SyncProgress.IncrementProcessed();
            return id;
        }

        SyncProgress.IncrementProcessed();
        return null;
    }
}

public record DiaryInfo(DateTime? LastDate, bool HasAnyEntry);
