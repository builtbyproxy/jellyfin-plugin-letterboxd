using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class JellyseerrClientTests
{
    private const string BaseUrl = "http://jellyseerr.test";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task RequestMovieAsync_NoExistingMedia_PostsRequest()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100}"); // no mediaInfo → safe to request

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var result = await client.RequestMovieAsync(100, 7);

        Assert.Equal(JellyseerrClient.RequestResult.Requested, result);
        Assert.True(posted, "request endpoint should have been called");
    }

    [Theory]
    [InlineData(2)] // PENDING
    [InlineData(3)] // PROCESSING
    [InlineData(4)] // PARTIALLY_AVAILABLE
    [InlineData(5)] // AVAILABLE
    [InlineData(6)] // BLOCKLISTED
    public async Task RequestMovieAsync_AlreadyKnownStatus_DoesNotPost(int status)
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse($"{{\"id\":100,\"mediaInfo\":{{\"status\":{status}}}}}");

            if (req.Method == HttpMethod.Post)
                posted = true;

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var result = await client.RequestMovieAsync(100, 7);

        Assert.Equal(JellyseerrClient.RequestResult.AlreadyExists, result);
        Assert.False(posted, "request endpoint must not be called when media is already known");
    }

    [Fact]
    public async Task RequestMovieAsync_DeletedStatus_RequestsAgain()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":7}}"); // DELETED

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var result = await client.RequestMovieAsync(100, 7);

        Assert.Equal(JellyseerrClient.RequestResult.Requested, result);
        Assert.True(posted);
    }

    [Fact]
    public async Task RequestMovieAsync_PostReturns409_TreatsAsAlreadyExists()
    {
        // Belt-and-braces: if the status pre-check misses (e.g. 404 lookup) and Jellyseerr
        // does return its own 409, we should still classify it as AlreadyExists so the
        // count of "new requests" stays accurate.
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (req.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"message\":\"REQUEST_EXISTS\"}")
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var result = await client.RequestMovieAsync(100, 7);

        Assert.Equal(JellyseerrClient.RequestResult.AlreadyExists, result);
    }

    [Fact]
    public async Task GetUserWatchlistTmdbIdsAsync_PaginatesViaPageParamAndFiltersToMovies()
    {
        // Jellyseerr paginates the user-watchlist endpoint with `page=N`, NOT take/skip
        // (which it 400s on). Drive two pages, with totalPages=2 telling us when to stop.
        var seenPageQueries = new List<string>();
        var handler = new SeerrHandler(req =>
        {
            seenPageQueries.Add(req.RequestUri!.Query);
            var path = req.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/watchlist")) return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (req.RequestUri!.Query.Contains("page=1"))
                return JsonResponse(
                    "{\"page\":1,\"totalPages\":2,\"totalResults\":3,\"results\":[" +
                    "{\"tmdbId\":1,\"mediaType\":\"movie\"}," +
                    "{\"tmdbId\":\"2\",\"mediaType\":\"movie\"}," +
                    "{\"tmdbId\":3,\"mediaType\":\"tv\"}" +
                    "]}");
            if (req.RequestUri!.Query.Contains("page=2"))
                return JsonResponse(
                    "{\"page\":2,\"totalPages\":2,\"totalResults\":3,\"results\":[" +
                    "{\"tmdbId\":4,\"mediaType\":\"movie\"}" +
                    "]}");
            return JsonResponse("{\"page\":99,\"totalPages\":2,\"results\":[]}");
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var ids = await client.GetUserWatchlistTmdbIdsAsync(7);

        Assert.Equal(new HashSet<int> { 1, 2, 4 }, ids);
        Assert.Contains(seenPageQueries, q => q.Contains("page=1"));
        Assert.Contains(seenPageQueries, q => q.Contains("page=2"));
        // Crucially, no take/skip — those would 400.
        Assert.DoesNotContain(seenPageQueries, q => q.Contains("take=") || q.Contains("skip="));
    }

    [Fact]
    public async Task GetUserWatchlistTmdbIdsAsync_SendsXApiUserHeader()
    {
        // The endpoint requires acting as the user being queried — without the impersonation
        // header, Jellyseerr returns the calling key's default user (admin), which silently
        // returns the wrong watchlist.
        string? sentHeader = null;
        var handler = new SeerrHandler(req =>
        {
            sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
            return JsonResponse("{\"page\":1,\"totalPages\":1,\"totalResults\":0,\"results\":[]}");
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        await client.GetUserWatchlistTmdbIdsAsync(42);
        Assert.Equal("42", sentHeader);
    }

    [Fact]
    public async Task AddToWatchlistAsync_SendsXApiUserHeaderAndCorrectBody()
    {
        string? sentBody = null;
        string? sentHeader = null;

        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/watchlist"))
            {
                sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var ok = await client.AddToWatchlistAsync(42, 9);

        Assert.True(ok);
        Assert.Equal("9", sentHeader);
        Assert.Contains("\"tmdbId\":42", sentBody);
        Assert.Contains("\"mediaType\":\"movie\"", sentBody);
    }

    [Fact]
    public async Task AddToWatchlistAsync_409Conflict_IsTreatedAsSuccess()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"message\":\"already exists on watchlist\"}")
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.AddToWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task RemoveFromWatchlistAsync_404IsSuccess()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.RemoveFromWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task RemoveFromWatchlistAsync_SendsCorrectUrlAndHeader()
    {
        string? sentPath = null;
        string? sentQuery = null;
        string? sentHeader = null;

        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                sentPath = req.RequestUri!.AbsolutePath;
                sentQuery = req.RequestUri!.Query;
                sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });

        using var client = new JellyseerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.RemoveFromWatchlistAsync(42, 9));
        Assert.Equal("/api/v1/watchlist/42", sentPath);
        Assert.Contains("mediaType=movie", sentQuery);
        Assert.Equal("9", sentHeader);
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private class SeerrHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public SeerrHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
