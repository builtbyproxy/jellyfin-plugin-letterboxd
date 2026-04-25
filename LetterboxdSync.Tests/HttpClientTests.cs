using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class HttpClientTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public void DefaultUserAgent_AppliedWhenNoOverride()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        Assert.Equal(LetterboxdHttpClient.DefaultUserAgent,
            http.Http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void CustomUserAgent_AppliedWhenProvided()
    {
        const string chromeUa = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        using var http = new LetterboxdHttpClient(TestLogger, chromeUa);
        Assert.Equal(chromeUa, http.Http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void InvalidUserAgent_FallsBackToDefault()
    {
        using var http = new LetterboxdHttpClient(TestLogger, "this is not a valid \0 ua");
        Assert.Equal(LetterboxdHttpClient.DefaultUserAgent,
            http.Http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void WhitespaceUserAgent_FallsBackToDefault()
    {
        using var http = new LetterboxdHttpClient(TestLogger, "   ");
        Assert.Equal(LetterboxdHttpClient.DefaultUserAgent,
            http.Http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void SetRawCookies_ParsesMultipleCookies()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        http.SetRawCookies("session=abc123; theme=dark; com.xk72.webparts.csrf=csrftoken");

        var cookies = http.CookieContainer.GetCookies(BaseUri);
        Assert.NotNull(cookies["session"]);
        Assert.Equal("abc123", cookies["session"]!.Value);
        Assert.Equal("csrftoken", http.Csrf);
    }

    [Fact]
    public void SetRawCookies_NullOrEmpty_NoOp()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        http.SetRawCookies(null);
        http.SetRawCookies("");
        http.SetRawCookies("   ");

        var cookies = http.CookieContainer.GetCookies(BaseUri);
        Assert.Equal(0, cookies.Count);
    }

    [Fact]
    public void SetRawCookies_UrlDecodesValues()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        http.SetRawCookies("name=hello%20world");

        var cookies = http.CookieContainer.GetCookies(BaseUri);
        Assert.Equal("hello world", cookies["name"]!.Value);
    }

    [Fact]
    public void HasAuthenticatedSession_WithUserCookie_ReturnsTrue()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        http.SetRawCookies("letterboxd.user.CURRENT=somevalue");

        Assert.True(http.HasAuthenticatedSession());
    }

    [Fact]
    public void HasAuthenticatedSession_WithoutUserCookie_ReturnsFalse()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        Assert.False(http.HasAuthenticatedSession());
    }

    [Fact]
    public void ClearSession_ExpiresAllCookies()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        http.SetRawCookies("letterboxd.user.CURRENT=session; com.xk72.webparts.csrf=token");

        Assert.True(http.HasAuthenticatedSession());

        http.ClearSession();

        Assert.False(http.HasAuthenticatedSession());
    }

    [Fact]
    public void ExtractHiddenInput_WithExtraAttributes_Works()
    {
        var html = "<input type=\"hidden\" id=\"csrf\" class=\"form-field\" name=\"__csrf\" value=\"tok123\" data-test=\"true\" />";
        var result = LetterboxdHttpClient.ExtractHiddenInput(html, "__csrf");
        Assert.Equal("tok123", result);
    }

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        Assert.Equal("hello", LetterboxdHttpClient.Truncate("hello", 300));
    }

    [Fact]
    public void Truncate_LongString_Truncates()
    {
        var long_string = new string('x', 500);
        Assert.Equal(300, LetterboxdHttpClient.Truncate(long_string, 300).Length);
    }

    [Fact]
    public async Task GetWithCloudflareRetry_Success_ReturnsImmediately()
    {
        int callCount = 0;
        var handler = new SimpleMockHandler((request) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });

        using var http = new LetterboxdHttpClient(TestLogger, handler);
        using var res = await http.GetWithCloudflareRetryAsync("/test");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetWithCloudflareRetry_Persistent403_ReturnsAfterMaxRetries()
    {
        int callCount = 0;
        var handler = new SimpleMockHandler((request) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });

        using var http = new LetterboxdHttpClient(TestLogger, handler);
        using var res = await http.GetWithCloudflareRetryAsync("/test", maxRetries: 2);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RefreshCsrfAsync_SetsCsrfFromCookie()
    {
        var handler = new SimpleMockHandler((request) =>
        {
            // The handler can't set cookies directly, but we can verify the request was made
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new LetterboxdHttpClient(TestLogger, handler);
        // Pre-set the cookie so RefreshCsrf can read it
        http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "refreshed-token"));

        await http.RefreshCsrfAsync();

        Assert.Equal("refreshed-token", http.Csrf);
    }

    private class SimpleMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public SimpleMockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
