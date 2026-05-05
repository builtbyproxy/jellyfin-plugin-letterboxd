using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Coverage for the scraper's slow paginated methods. Each invocation of
/// GetWatchlist/GetDiary kicks a 2-4s real-time delay before the first request,
/// so these tests are necessarily slower than the unit tests above. They run
/// fine inside a single page (one delay per test).
///
/// We pre-seed TmdbCache for the slugs in the test fixtures so the per-slug
/// resolver short-circuits the cache hit path instead of doing another 2-3s
/// HTTP round-trip per slug — without this, a 5-film test would take ~20s.
/// </summary>
[Collection("TmdbCache")]
public class ScraperAsyncMethodsTests : IDisposable
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");
    private readonly string _tempCachePath;

    public ScraperAsyncMethodsTests()
    {
        _tempCachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "lbs-scraper-tmdb-" + Guid.NewGuid().ToString("N") + ".json");
        TmdbCache.CachePathOverride = _tempCachePath;
        TmdbCache.ResetForTesting();
    }

    public void Dispose()
    {
        TmdbCache.CachePathOverride = null;
        TmdbCache.ResetForTesting();
        try { if (System.IO.File.Exists(_tempCachePath)) System.IO.File.Delete(_tempCachePath); } catch { }
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_SinglePage_ReturnsResolvedTmdbIds()
    {
        // Pre-seed cache so ResolveTmdbIdFromSlugAsync skips the per-slug HTTP+delay.
        TmdbCache.Set("dune-part-two", 693134);
        TmdbCache.Set("oppenheimer", 872585);

        var handler = new MockHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path == "/8bitproxy/watchlist/page/1/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><body>" +
                        "<div data-component-class='LazyPoster' data-item-slug='dune-part-two'></div>" +
                        "<div data-component-class='LazyPoster' data-item-slug='oppenheimer'></div>" +
                        "</body></html>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ids = await scraper.GetWatchlistTmdbIdsAsync("8bitproxy");

        Assert.Equal(new[] { 693134, 872585 }, ids.ToArray());
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_NoPosters_StopsImmediately()
    {
        var handler = new MockHandler((request, _) =>
        {
            // First page has no LazyPoster nodes, scraper exits the page loop.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>nothing here</body></html>")
            };
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ids = await scraper.GetWatchlistTmdbIdsAsync("emptywatchlist");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_CloudflareChallenge_StopsCleanly()
    {
        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Just a moment...</body></html>")
            });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ids = await scraper.GetWatchlistTmdbIdsAsync("blocked");

        // The challenge detection path returns whatever was found before (zero),
        // logs a warning, and exits the loop without crashing.
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_NonSuccessStatus_ReturnsPartial()
    {
        // Simulate a 503 on the first page; the scraper should log and exit
        // the loop, returning whatever it had collected so far (zero here).
        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ids = await scraper.GetWatchlistTmdbIdsAsync("blocked");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_SinglePage_ParsesRatingFromClass()
    {
        TmdbCache.Set("sinners-2025", 1233413);
        TmdbCache.Set("luca", 508943);

        var handler = new MockHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path == "/8bitproxy/films/page/1/")
            {
                // The films page wraps each poster in an li.poster-container; the
                // rating, when set, lives in a child span with a rated-N class.
                // Sinners gets 4 stars (rated-8), Luca gets none.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><body>" +
                        "<li class='poster-container'>" +
                        "<div data-film-slug='sinners-2025' data-component-class='LazyPoster'></div>" +
                        "<span class='rating rated-8'>★★★★</span>" +
                        "</li>" +
                        "<li class='poster-container'>" +
                        "<div data-film-slug='luca' data-component-class='LazyPoster'></div>" +
                        "</li>" +
                        "</body></html>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var entries = await scraper.GetDiaryFilmEntriesAsync("8bitproxy");

        Assert.Equal(2, entries.Count);
        var sinners = entries.First(e => e.TmdbId == 1233413);
        Assert.Equal(4.0, sinners.Rating);  // rated-8 → 4.0 stars (Letterboxd scale)
        var luca = entries.First(e => e.TmdbId == 508943);
        Assert.Null(luca.Rating);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_NoPosters_StopsImmediately()
    {
        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>empty</body></html>")
            });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var entries = await scraper.GetDiaryFilmEntriesAsync("nothing");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_CloudflareChallenge_StopsCleanly()
    {
        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Attention Required</body></html>")
            });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var entries = await scraper.GetDiaryFilmEntriesAsync("blocked");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_NonSuccessStatus_ReturnsPartial()
    {
        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var entries = await scraper.GetDiaryFilmEntriesAsync("ghost");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetDiaryTmdbIdsAsync_DelegatesToFilmEntries_ReturnsJustIds()
    {
        // GetDiaryTmdbIdsAsync is a thin wrapper around GetDiaryFilmEntriesAsync;
        // make sure it strips ratings and just returns the TMDb ids.
        TmdbCache.Set("oppenheimer", 872585);

        var handler = new MockHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>" +
                    "<li class='poster-container'>" +
                    "<div data-film-slug='oppenheimer' data-component-class='LazyPoster'></div>" +
                    "<span class='rating rated-10'>★★★★★</span>" +
                    "</li>" +
                    "</body></html>")
            });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ids = await scraper.GetDiaryTmdbIdsAsync("8bitproxy");

        Assert.Equal(new[] { 872585 }, ids.ToArray());
    }

    /// <summary>Inline mock handler so we don't depend on ScraperTests.ScraperMockHandler being accessible.</summary>
    private class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public MockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
            => _responder = responder;

        public (LetterboxdHttpClient Http, LetterboxdScraper Scraper) CreateClients(ILogger logger)
        {
            var http = new LetterboxdHttpClient(logger, this);
            _httpClient = http;
            return (http, new LetterboxdScraper(http, logger));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _httpClient!));
    }
}
