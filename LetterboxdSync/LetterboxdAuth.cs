using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Handles Letterboxd authentication: login, session management, re-auth.
/// </summary>
public class LetterboxdAuth
{
    private readonly LetterboxdHttpClient _http;
    private readonly ILogger _logger;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _hasReauthenticated;

    public LetterboxdAuth(LetterboxdHttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Username => _username;

    /// <summary>
    /// Reset the re-auth guard so future 401s can trigger re-authentication.
    /// Call this after each successful operation.
    /// </summary>
    public void ResetReauthGuard()
    {
        _hasReauthenticated = false;
    }

    /// <summary>
    /// Returns true if a re-auth attempt should be made (hasn't already been tried).
    /// Sets the guard so it won't try again until reset.
    /// </summary>
    public bool ShouldReauthenticate()
    {
        if (_hasReauthenticated) return false;
        _hasReauthenticated = true;
        return true;
    }

    public async Task ForceReauthenticateAsync(string username, string password)
    {
        _http.ClearSession();
        _logger.LogInformation("Forcing fresh login for {Username}", username);
        await AuthenticateAsync(username, password).ConfigureAwait(false);
    }

    public async Task ForceReauthenticateAsync()
    {
        await ForceReauthenticateAsync(_username, _password).ConfigureAwait(false);
    }

    public async Task AuthenticateAsync(string username, string password)
    {
        _username = username;
        _password = password;

        if (_http.HasAuthenticatedSession())
        {
            try
            {
                await _http.RefreshCsrfAsync().ConfigureAwait(false);
                _logger.LogInformation("Reusing existing session for {Username}", username);
                return;
            }
            catch
            {
                _logger.LogWarning("Existing session expired for {Username}, performing fresh login", username);
            }
        }

        await Task.Delay(500 + Random.Shared.Next(1000)).ConfigureAwait(false);

        string signInCsrf = await FetchSignInCsrfAsync().ConfigureAwait(false);
        _http.Csrf = signInCsrf;

        await Task.Delay(3000 + Random.Shared.Next(4000)).ConfigureAwait(false);
        await SubmitLoginAsync(username, password).ConfigureAwait(false);

        await _http.RefreshCsrfAsync().ConfigureAwait(false);
        _logger.LogInformation("Authenticated with Letterboxd as {Username}", username);
    }

    private async Task<string> FetchSignInCsrfAsync()
    {
        using var signInReq = new HttpRequestMessage(HttpMethod.Get, "/sign-in/");
        _http.SetNavHeaders(signInReq.Headers);
        using var signInRes = await _http.Http.SendAsync(signInReq).ConfigureAwait(false);

        if (signInRes.StatusCode == HttpStatusCode.Forbidden)
        {
            return await HandleCloudflareSignInAsync().ConfigureAwait(false);
        }

        signInRes.EnsureSuccessStatusCode();
        var html = await signInRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        return LetterboxdHttpClient.ExtractHiddenInput(html, "__csrf")
            ?? throw new Exception("Could not find __csrf on /sign-in/.");
    }

    private async Task<string> HandleCloudflareSignInAsync()
    {
        if (!_http.CookieContainer.GetCookies(LetterboxdHttpClient.BaseUri).Cast<Cookie>()
            .Any(c => c.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase)))
        {
            throw new Exception("Letterboxd returned 403 on /sign-in/. Likely Cloudflare. Provide raw cookies with cf_clearance.");
        }

        await Task.Delay(1500).ConfigureAwait(false);
        using var warmup = new HttpRequestMessage(HttpMethod.Get, "/");
        _http.SetNavHeaders(warmup.Headers);
        using var _ = await _http.Http.SendAsync(warmup).ConfigureAwait(false);

        using var retryReq = new HttpRequestMessage(HttpMethod.Get, "/sign-in/");
        _http.SetNavHeaders(retryReq.Headers);
        using var retryRes = await _http.Http.SendAsync(retryReq).ConfigureAwait(false);

        if (retryRes.StatusCode == HttpStatusCode.Forbidden)
            throw new Exception("Letterboxd returned 403 even with Cloudflare cookies. Try refreshing your raw cookies.");

        var retryHtml = await retryRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        return LetterboxdHttpClient.ExtractHiddenInput(retryHtml, "__csrf")
            ?? throw new Exception("Could not find __csrf on /sign-in/ after Cloudflare retry.");
    }

    private async Task SubmitLoginAsync(string username, string password)
    {
        using var loginReq = new HttpRequestMessage(HttpMethod.Post, "/user/login.do");
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
            { "__csrf", _http.Csrf },
            { "remember", "true" },
            { "authenticationCode", "" }
        });

        using var loginRes = await _http.Http.SendAsync(loginReq).ConfigureAwait(false);

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
}
