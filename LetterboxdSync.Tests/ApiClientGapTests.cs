using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Covers the LetterboxdApiClient branches the happy-path tests don't reach:
/// the not-authenticated guard, error responses on diary writes, token refresh
/// and refresh-fallback, the test-only cleanup helper, and slug fallbacks.
/// Reuses ApiMockHandler / ApiTestHelpers from ApiClientTests.
/// </summary>
public class ApiClientGapTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task MarkAsWatchedAsync_NotAuthenticated_Throws()
    {
        using var client = new LetterboxdApiClient(TestLogger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.MarkAsWatchedAsync("fight-club", "2a9q", DateTime.Now, liked: false));
    }

    [Fact]
    public async Task GetDiaryInfoAsync_NonSuccess_ReturnsEmpty()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true &&
                request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var info = await client.GetDiaryInfoAsync("2a9q", "user");

        Assert.False(info.HasAnyEntry);
        Assert.Null(info.LastDate);
    }

    [Fact]
    public async Task MarkAsWatchedAsync_Unauthorized_ClearsTokenAndThrows()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.MarkAsWatchedAsync("film", "lid", DateTime.Now, liked: false));
        Assert.Contains("token expired", ex.Message);
    }

    [Fact]
    public async Task MarkAsWatchedAsync_ServerError_Throws()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("validation failed")
                };
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.MarkAsWatchedAsync("film", "lid", DateTime.Now, liked: false));
        Assert.Contains("Failed to log film", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredCachedToken_RefreshesInsteadOfFullAuth()
    {
        var grantTypes = new List<string>();
        var handler = new ApiMockHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/auth/token"))
            {
                var body = request.Content?.ReadAsStringAsync().Result ?? "";
                grantTypes.Add(body.Contains("grant_type=refresh_token") ? "refresh" : "password");
                // First (password) grant expires almost immediately so the second
                // AuthenticateAsync sees an expired cache entry and refreshes.
                var token = body.Contains("grant_type=refresh_token") ? "refreshed" : "initial";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = token,
                        expires_in = 1,
                        refresh_token = "rt-123"
                    }))
                };
            }
            if (path.EndsWith("/me"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"member\":{\"id\":\"m1\",\"username\":\"u\"}}")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var username = "refresh_" + Guid.NewGuid().ToString("N");
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync(username, "pass"); // full password auth
        await client.AuthenticateAsync(username, "pass"); // cache expired → refresh

        Assert.Equal("password", grantTypes[0]);
        Assert.Contains("refresh", grantTypes);
    }

    [Fact]
    public async Task AuthenticateAsync_RefreshFails_FallsBackToFullAuth()
    {
        var grantTypes = new List<string>();
        var handler = new ApiMockHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/auth/token"))
            {
                var body = request.Content?.ReadAsStringAsync().Result ?? "";
                var isRefresh = body.Contains("grant_type=refresh_token");
                grantTypes.Add(isRefresh ? "refresh" : "password");
                // Refresh grant fails → client must fall back to a fresh password auth.
                if (isRefresh)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"invalid_grant\"}")
                    };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "ok",
                        expires_in = 1,
                        refresh_token = "rt-123"
                    }))
                };
            }
            if (path.EndsWith("/me"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"member\":{\"id\":\"m1\",\"username\":\"u\"}}")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var username = "refreshfail_" + Guid.NewGuid().ToString("N");
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync(username, "pass"); // full auth, caches refresh token
        await client.AuthenticateAsync(username, "pass"); // refresh attempted then falls back

        // Second call tried refresh, then a second password grant.
        Assert.Contains("refresh", grantTypes);
        Assert.Equal(2, grantTypes.Count(g => g == "password"));
    }

    [Fact]
    public async Task DeleteAllLogEntriesForFilmAsync_DeletesEveryEntry()
    {
        var deleted = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path.EndsWith("/log-entries"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"items\":[{\"id\":\"e1\"},{\"id\":\"e2\"}]}")
                };
            if (request.Method == HttpMethod.Delete && path.Contains("/log-entry/"))
            {
                deleted.Add(path.Split("/log-entry/").Last());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.DeleteAllLogEntriesForFilmAsync("lid-1");

        Assert.Equal(new[] { "e1", "e2" }, deleted);
    }

    [Fact]
    public async Task LookupFilmByTmdbIdAsync_NoLetterboxdLink_FallsBackToTopLevelLink()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
                // No links[].type == "letterboxd"; slug must come from top-level "link".
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                id = "xyz1",
                                link = "https://letterboxd.com/film/the-thing/",
                                links = new[] { new { type = "tmdb", id = "1091", url = "https://www.themoviedb.org/movie/1091" } }
                            }
                        }
                    }))
                };
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var result = await client.LookupFilmByTmdbIdAsync(1091);

        Assert.Equal("xyz1", result.FilmId);
        Assert.Equal("the-thing", result.Slug);
    }
}
