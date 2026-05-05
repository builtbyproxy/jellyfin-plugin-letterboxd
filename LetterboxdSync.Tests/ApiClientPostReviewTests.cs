using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for LetterboxdApiClient.PostReviewAsync, the official-API path for review
/// posting. Unlike the scraper variant in PostReviewTests, this endpoint requires a
/// TMDb ID (it resolves to LID via /films) and posts straight to /log-entries.
/// </summary>
public class ApiClientPostReviewTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    private static HttpResponseMessage FilmLookupResponse(int tmdbId, string lid)
    {
        var body = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    id = lid,
                    name = "Sinners",
                    link = "https://letterboxd.com/film/sinners-2025/",
                    links = new[]
                    {
                        new { type = "letterboxd", id = lid, url = $"https://letterboxd.com/film/sinners-2025/" },
                        new { type = "tmdb", id = tmdbId.ToString(), url = $"https://themoviedb.org/movie/{tmdbId}" }
                    }
                }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    [Fact]
    public async Task PostReviewAsync_WithoutTmdbId_Throws()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler();
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.PostReviewAsync("sinners-2025", "review", tmdbId: null));

        Assert.Contains("TMDb ID", ex.Message);
    }

    [Fact]
    public async Task PostReviewAsync_TmdbIdZero_Throws()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler();
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.PostReviewAsync("sinners-2025", "review", tmdbId: 0));

        Assert.Contains("TMDb ID", ex.Message);
    }

    [Fact]
    public async Task PostReviewAsync_WithRating_IncludesRatingInBody()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/films"))
                return FilmLookupResponse(1233413, "KQMM");
            if (request.Method == HttpMethod.Post && path.EndsWith("/log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.PostReviewAsync("sinners-2025", "great", rating: 4.5, tmdbId: 1233413);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("KQMM", root.GetProperty("filmId").GetString());
        Assert.Equal(4.5, root.GetProperty("rating").GetDouble());
        Assert.Equal("great", root.GetProperty("review").GetProperty("text").GetString());
        Assert.False(root.GetProperty("review").GetProperty("containsSpoilers").GetBoolean());
    }

    [Fact]
    public async Task PostReviewAsync_RewatchWithoutText_PostsRewatchFlagOnly()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/films"))
                return FilmLookupResponse(1233413, "KQMM");
            if (request.Method == HttpMethod.Post && path.EndsWith("/log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.PostReviewAsync("sinners-2025", reviewText: null, isRewatch: true,
            date: "2026-04-29", tmdbId: 1233413);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("diaryDetails").GetProperty("rewatch").GetBoolean());
        Assert.Equal("2026-04-29", root.GetProperty("diaryDetails").GetProperty("diaryDate").GetString());
        Assert.False(root.TryGetProperty("review", out _));
    }

    [Fact]
    public async Task PostReviewAsync_ContainsSpoilers_FlaggedInBody()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/films"))
                return FilmLookupResponse(1233413, "KQMM");
            if (request.Method == HttpMethod.Post && path.EndsWith("/log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.PostReviewAsync("sinners-2025", "spoilers", containsSpoilers: true, tmdbId: 1233413);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("review").GetProperty("containsSpoilers").GetBoolean());
    }

    [Fact]
    public async Task PostReviewAsync_4xxResponse_ThrowsWithStatusCode()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/films"))
                return FilmLookupResponse(1233413, "KQMM");
            if (request.Method == HttpMethod.Post && path.EndsWith("/log-entries"))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"bad payload\"}")
                };
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.PostReviewAsync("sinners-2025", "review", tmdbId: 1233413));

        Assert.Contains("Failed to post review", ex.Message);
        Assert.Contains("BadRequest", ex.Message);
    }

    [Fact]
    public async Task PostReviewAsync_DateOmitted_UsesTodaysDate()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/films"))
                return FilmLookupResponse(1233413, "KQMM");
            if (request.Method == HttpMethod.Post && path.EndsWith("/log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.PostReviewAsync("sinners-2025", "x", date: null, tmdbId: 1233413);

        Assert.NotNull(capturedBody);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(today, doc.RootElement.GetProperty("diaryDetails").GetProperty("diaryDate").GetString());
    }
}
