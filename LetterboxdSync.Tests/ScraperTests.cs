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

public class ScraperTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task LookupFilmByTmdbId_ValidFilm_ReturnsFilmResult()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // TMDb redirect page — returns HTML with canonical link
            if (path.StartsWith("/tmdb/693134"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/dune-part-two/\" /></head></html>")
                };
            }

            // Film page — returns HTML with data-film-id
            if (path == "/film/dune-part-two/")
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<div data-film-slug=\"dune-part-two\" data-film-id=\"945898\"></div>")
                };
                res.Headers.TryAddWithoutValidation("x-letterboxd-identifier", "PROD-dune");
                return res;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var result = await scraper.LookupFilmByTmdbIdAsync(693134);

        Assert.Equal("dune-part-two", result.Slug);
        Assert.Equal("945898", result.FilmId);
        Assert.Equal("PROD-dune", result.ProductionId);
    }

    [Fact]
    public async Task LookupFilmByTmdbId_NotFound_Throws()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        await Assert.ThrowsAsync<Exception>(() => scraper.LookupFilmByTmdbIdAsync(99999));
    }

    [Fact]
    public async Task LookupFilmByTmdbId_403ThenSuccess_RetriesWithBackoff()
    {
        int tmdbCallCount = 0;

        var handler = new ScraperMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (path.StartsWith("/tmdb/"))
            {
                tmdbCallCount++;
                if (tmdbCallCount == 1)
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/test-film/\" /></head></html>")
                };
            }

            if (path == "/film/test-film/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<div data-film-slug=\"test-film\" data-film-id=\"111\"></div>")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var result = await scraper.LookupFilmByTmdbIdAsync(12345);

        Assert.Equal("test-film", result.Slug);
        Assert.True(tmdbCallCount >= 2, $"Expected retry, got {tmdbCallCount} calls");
    }

    [Fact]
    public void ExtractFilmIdentifiers_HeaderAndHtml_ExtractsBoth()
    {
        var html = "<div data-film-slug=\"gladiator-ii\" data-film-id=\"54321\"></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-letterboxd-identifier", "PROD-glad");

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "gladiator-ii", response.Headers);

        Assert.Equal("54321", filmId);
        Assert.Equal("PROD-glad", productionId);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NoHeader_FallsBackToPosteredIdentifier()
    {
        var html = @"<div data-postered-identifier='{""lid"":""PROD-fallback""}' />
                     <div data-film-slug=""test"" data-film-id=""999""></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "test");

        Assert.Equal("999", filmId);
        Assert.Equal("PROD-fallback", productionId);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NoFilmElement_Throws()
    {
        var html = "<html><body><p>No film here</p></body></html>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        Assert.Throws<Exception>(() => scraper.ExtractFilmIdentifiers(html, "missing-film"));
        http.Dispose();
    }

    [Fact]
    public async Task GetDiaryInfo_NoDiaryEntries_ReturnsEmpty()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>No diary entries</body></html>")
            };
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("test-film", "testuser");

        Assert.Null(info.LastDate);
        Assert.False(info.HasAnyEntry);
    }

    [Fact]
    public async Task GetDiaryInfo_WithEntries_ReturnsMostRecent()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"
                    <a class=""month"">Mar</a>
                    <a class=""month"">Jan</a>
                    <a class=""date"">25</a>
                    <a class=""date"">10</a>
                    <a class=""year"">2026</a>
                    <a class=""year"">2026</a>")
            };
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("test-film", "testuser");

        Assert.True(info.HasAnyEntry);
        Assert.NotNull(info.LastDate);
    }

    [Fact]
    public async Task GetDiaryInfo_404_ReturnsEmpty()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("nonexistent", "testuser");

        Assert.Null(info.LastDate);
        Assert.False(info.HasAnyEntry);
    }

    internal class ScraperMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public ScraperMockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
            => _responder = responder;

        public (LetterboxdHttpClient Http, LetterboxdScraper Scraper) CreateClients(ILogger logger)
        {
            var http = new LetterboxdHttpClient(logger, this);
            _httpClient = http;
            var scraper = new LetterboxdScraper(http, logger);
            return (http, scraper);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _httpClient!));
    }
}
