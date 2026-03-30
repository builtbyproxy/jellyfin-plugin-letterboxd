using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Shared HTTP client for Letterboxd with cookie management, CSRF handling,
/// and Cloudflare retry logic. Designed to be used as a singleton.
/// </summary>
public class LetterboxdHttpClient : IDisposable
{
    internal static readonly Uri BaseUri = new Uri("https://letterboxd.com/");
    internal const int MaxRetries = 3;

    private readonly ILogger _logger;
    internal readonly CookieContainer CookieContainer = new();
    private readonly HttpClientHandler _handler;
    internal readonly HttpClient Http;
    internal string Csrf = string.Empty;

    public LetterboxdHttpClient(ILogger logger)
        : this(logger, null)
    {
    }

    internal LetterboxdHttpClient(ILogger logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _handler = handler as HttpClientHandler ?? new HttpClientHandler
        {
            CookieContainer = CookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        Http = handler != null
            ? new HttpClient(handler, disposeHandler: false) { BaseAddress = BaseUri }
            : new HttpClient(_handler) { BaseAddress = BaseUri };

        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:134.0) Gecko/20100101 Firefox/134.0");
        Http.DefaultRequestHeaders.Accept.Clear();
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        Http.DefaultRequestHeaders.Connection.Add("keep-alive");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "document");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "none");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-user", "?1");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("upgrade-insecure-requests", "1");
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
                CookieContainer.Add(BaseUri, new Cookie(name, val, "/", "letterboxd.com"));
                CookieContainer.Add(BaseUri, new Cookie(name, val, "/", ".letterboxd.com"));

                if (string.Equals(name, "com.xk72.webparts.csrf", StringComparison.OrdinalIgnoreCase))
                    Csrf = val;
            }
            catch { }
        }
    }

    internal bool HasAuthenticatedSession()
    {
        var cookies = CookieContainer.GetCookies(BaseUri);
        return !string.IsNullOrWhiteSpace(cookies["letterboxd.user.CURRENT"]?.Value);
    }

    internal string GetCsrfFromCookie()
    {
        var cookies = CookieContainer.GetCookies(BaseUri);
        return cookies["com.xk72.webparts.csrf"]?.Value ?? string.Empty;
    }

    internal async Task RefreshCsrfAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        SetNavHeaders(request.Headers);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);

        var token = GetCsrfFromCookie();
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Could not read CSRF cookie after refresh.");
        Csrf = token;
    }

    internal void ClearSession()
    {
        foreach (Cookie cookie in CookieContainer.GetCookies(BaseUri))
            cookie.Expired = true;
    }

    internal void SetNavHeaders(HttpRequestHeaders headers, string site = "none", string? referrer = null)
    {
        headers.TryAddWithoutValidation("sec-fetch-dest", "document");
        headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        headers.TryAddWithoutValidation("sec-fetch-site", site);
        headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");

        if (referrer != null)
            headers.Referrer = new Uri(referrer);
    }

    internal void SetApiHeaders(HttpRequestMessage req, string filmSlug)
    {
        req.Headers.Referrer = new Uri($"https://letterboxd.com/film/{filmSlug}/");
        req.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", Csrf);
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    internal static string? ExtractHiddenInput(string html, string name)
    {
        var pattern = $@"<input[^>]*\bname\s*=\s*[""{Regex.Escape(name)}""][^>]*\bvalue\s*=\s*[""']([^""']*)[""'][^>]*>";
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    internal static string Truncate(string s, int max = 300)
        => s.Length > max ? s.Substring(0, max) : s;

    /// <summary>
    /// Retry an HTTP GET with Cloudflare 403 backoff.
    /// </summary>
    internal async Task<HttpResponseMessage> GetWithCloudflareRetryAsync(
        string path, string site = "same-origin", string? referrer = null, int maxRetries = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            SetNavHeaders(req.Headers, site, referrer);
            var res = await Http.SendAsync(req).ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.Forbidden && attempt < maxRetries - 1)
            {
                var backoff = (attempt + 1) * 15000 + Random.Shared.Next(10000);
                _logger.LogWarning("GET {Path} got 403 (Cloudflare). Backing off {Delay}ms (attempt {Attempt}/{Max})",
                    path, backoff, attempt + 1, maxRetries);
                await Task.Delay(backoff).ConfigureAwait(false);
                continue;
            }

            return res;
        }
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
    }
}
