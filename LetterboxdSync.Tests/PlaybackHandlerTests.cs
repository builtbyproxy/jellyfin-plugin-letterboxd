using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class ServiceRegistratorTests
{
    [Fact]
    public void RegisterServices_AddsPlaybackHandlerAsHostedService()
    {
        var services = new ServiceCollection();
        var registrator = new ServiceRegistrator();

        registrator.RegisterServices(services, null!);

        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(Microsoft.Extensions.Hosting.IHostedService), descriptor.ServiceType);
        Assert.Equal(typeof(PlaybackHandler), descriptor.ImplementationType);
    }
}

public class ReauthTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task MarkAsWatched_401ThenSuccess_ReauthenticatesAndRetries()
    {
        int logEntryCallCount = 0;
        bool didLogin = false;

        var handler = new MockHandler((request, httpClient) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // CSRF refresh
            if (request.Method == HttpMethod.Get && path == "/")
            {
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "testtoken"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            // Login page
            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<input type=\"hidden\" name=\"__csrf\" value=\"formtoken\" />")
                };
            }

            // Login POST
            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                didLogin = true;
                httpClient.CookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "newSession"));
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "newcsrf"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            // production-log-entries — first call returns 401, second returns 200
            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                logEntryCallCount++;
                if (logEntryCallCount == 1)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (httpClient, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = httpClient;

        // Set stale cookies so HasAuthenticatedSession() returns true initially
        httpClient.SetRawCookies("letterboxd.user.CURRENT=staleSession; com.xk72.webparts.csrf=stalecsrf");
        await auth.AuthenticateAsync("testuser", "testpass");

        // Should succeed after auto re-auth
        await diary.MarkAsWatchedAsync("test-film", "12345", DateTime.Now, false, "PROD1");

        Assert.True(logEntryCallCount >= 2, $"Expected at least 2 calls to production-log-entries, got {logEntryCallCount}");
        Assert.True(didLogin, "Expected a fresh login after 401");
    }

    [Fact]
    public async Task ForceReauthenticate_ClearsSessionAndLogs()
    {
        int loginCount = 0;

        var handler = new MockHandler((request, httpClient) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "testtoken"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<input type=\"hidden\" name=\"__csrf\" value=\"formtoken\" />")
                };
            }

            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                loginCount++;
                httpClient.CookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "freshSession"));
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "freshcsrf"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (httpClient, auth, _, _) = handler.CreateClients(TestLogger);
        using var __ = httpClient;

        httpClient.SetRawCookies("letterboxd.user.CURRENT=staleSession; com.xk72.webparts.csrf=stalecsrf");

        await auth.AuthenticateAsync("testuser", "testpass");

        // First auth should reuse session (no login call)
        Assert.Equal(0, loginCount);

        // Force re-auth should do a fresh login
        await auth.ForceReauthenticateAsync("testuser", "testpass");

        Assert.Equal(1, loginCount);
    }

    [Fact]
    public async Task ReauthGuard_ResetsAfterSuccessfulOperation()
    {
        int logEntryCallCount = 0;
        int loginCount = 0;

        var handler = new MockHandler((request, httpClient) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "testtoken"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<input type=\"hidden\" name=\"__csrf\" value=\"formtoken\" />")
                };
            }

            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                loginCount++;
                httpClient.CookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "session"));
                httpClient.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                logEntryCallCount++;
                // First and third calls return 401, second and fourth return 200
                if (logEntryCallCount % 2 == 1)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (httpClient, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = httpClient;

        httpClient.SetRawCookies("letterboxd.user.CURRENT=session; com.xk72.webparts.csrf=csrf");
        await auth.AuthenticateAsync("testuser", "testpass");

        // First call: 401 -> re-auth -> retry -> success
        await diary.MarkAsWatchedAsync("film-1", "111", DateTime.Now, false, "PROD1");
        Assert.Equal(1, loginCount);

        // Second call: 401 -> should re-auth again (guard was reset)
        await diary.MarkAsWatchedAsync("film-2", "222", DateTime.Now, false, "PROD2");
        Assert.Equal(2, loginCount);
    }

    /// <summary>
    /// Mock handler that receives the LetterboxdHttpClient so it can manipulate cookies directly.
    /// </summary>
    internal class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public MockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
            => _responder = responder;

        public (LetterboxdHttpClient Http, LetterboxdAuth Auth, LetterboxdScraper Scraper, LetterboxdDiary Diary) CreateClients(ILogger logger)
        {
            var http = new LetterboxdHttpClient(logger, this);
            _httpClient = http;
            var auth = new LetterboxdAuth(http, logger);
            var scraper = new LetterboxdScraper(http, logger);
            var diary = new LetterboxdDiary(http, auth, scraper, logger);
            return (http, auth, scraper, diary);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _httpClient!));
    }
}
