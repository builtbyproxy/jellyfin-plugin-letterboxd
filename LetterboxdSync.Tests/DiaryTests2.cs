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

public class DiaryOperationTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task MarkAsWatched_ProductionEndpointSuccess_Returns()
    {
        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        await diary.MarkAsWatchedAsync("test-film", "123", DateTime.Now, false, "PROD1");
        // No exception = success
    }

    [Fact]
    public async Task MarkAsWatched_ProductionEndpoint404_FallsBackToLogEntries()
    {
        bool hitLogEntries = false;

        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post && path.Contains("/api/v0/log-entries"))
            {
                hitLogEntries = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        await diary.MarkAsWatchedAsync("test-film", "123", DateTime.Now, false);

        Assert.True(hitLogEntries, "Expected fallback to /api/v0/log-entries");
    }

    [Fact]
    public async Task MarkAsWatched_403_ThrowsAntiBotException()
    {
        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        var ex = await Assert.ThrowsAsync<Exception>(
            () => diary.MarkAsWatchedAsync("test-film", "123", DateTime.Now, false, "PROD1"));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task MarkAsWatched_BothEndpoints404_ThrowsAfterRetries()
    {
        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        var ex = await Assert.ThrowsAsync<Exception>(
            () => diary.MarkAsWatchedAsync("test-film", "123", DateTime.Now, false));

        Assert.Contains("Failed to log", ex.Message);
    }

    [Fact]
    public async Task MarkAsWatched_WithRating_IncludesInPayload()
    {
        string? capturedBody = null;

        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        await diary.MarkAsWatchedAsync("test-film", "123", DateTime.Now, true, "PROD1", false, 4.5);

        Assert.NotNull(capturedBody);
        Assert.Contains("4.5", capturedBody!);
        Assert.Contains("\"like\":true", capturedBody);
        Assert.Contains("PROD1", capturedBody);
    }

    [Fact]
    public async Task MarkAsWatched_Rewatch_IncludesInPayload()
    {
        string? capturedBody = null;

        var handler = new DiaryMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.Contains("production-log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth, scraper, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        http.Csrf = "csrf";

        await diary.MarkAsWatchedAsync("test-film", "123", new DateTime(2026, 3, 25), false, "PROD1", rewatch: true);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"rewatch\":true", capturedBody!);
        Assert.Contains("2026-03-25", capturedBody);
    }

    internal class DiaryMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public DiaryMockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
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
