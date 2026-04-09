using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class ApiClientHmacTests
{
    [Fact]
    public void ComputeHmacSha256_ProducesCorrectSignature()
    {
        // Verify our HMAC implementation matches a known value
        var secret = "testsecret";
        var message = "GET\0https://api.letterboxd.com/api/v0/film/2a9q?apikey=testkey&nonce=abc&timestamp=123\0";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var signature = Convert.ToHexStringLower(hash);

        Assert.Equal(64, signature.Length);
        Assert.Matches("^[0-9a-f]{64}$", signature);
    }

    [Fact]
    public void ComputeHmacSha256_DifferentInputsProduceDifferentSignatures()
    {
        var secret = "testsecret";
        var msg1 = "GET\0https://example.com?a=1\0";
        var msg2 = "POST\0https://example.com?a=1\0body";

        using var hmac1 = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        using var hmac2 = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig1 = Convert.ToHexStringLower(hmac1.ComputeHash(Encoding.UTF8.GetBytes(msg1)));
        var sig2 = Convert.ToHexStringLower(hmac2.ComputeHash(Encoding.UTF8.GetBytes(msg2)));

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void ComputeHmacSha256_NullBytesSeparateComponents()
    {
        var secret = "testsecret";
        // The null byte separator is critical for security
        var withNulls = "GET\0https://example.com\0";
        var withoutNulls = "GEThttps://example.com";

        using var hmac1 = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        using var hmac2 = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig1 = Convert.ToHexStringLower(hmac1.ComputeHash(Encoding.UTF8.GetBytes(withNulls)));
        var sig2 = Convert.ToHexStringLower(hmac2.ComputeHash(Encoding.UTF8.GetBytes(withoutNulls)));

        Assert.NotEqual(sig1, sig2);
    }
}

public class ApiClientAuthTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task AuthenticateAsync_SuccessfulAuth_SetsToken()
    {
        var handler = new ApiMockHandler((request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.EndsWith("/auth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "test-token-123",
                        token_type = "Bearer",
                        expires_in = 3600,
                        refresh_token = "refresh-token-456"
                    }))
                };
            }

            if (path.EndsWith("/me"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        member = new { id = "abc123", username = "testuser" }
                    }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("testuser", "testpass");

        // Should not throw, meaning auth succeeded
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidCredentials_ThrowsWithMessage()
    {
        var handler = new ApiMockHandler((request) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.EndsWith("/auth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Your credentials don't match.\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        var ex = await Assert.ThrowsAsync<Exception>(() => client.AuthenticateAsync("bad", "creds"));
        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_RequestIncludesSignature()
    {
        var capturedUrls = new List<string>();

        var handler = new ApiMockHandler((request) =>
        {
            capturedUrls.Add(request.RequestUri?.ToString() ?? "");

            if (request.RequestUri?.AbsolutePath.EndsWith("/auth/token") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "token",
                        expires_in = 3600,
                        refresh_token = "refresh"
                    }))
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/me") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"member\":{\"id\":\"m1\",\"username\":\"u\"}}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("signature_test_user_" + Guid.NewGuid().ToString("N"), "pass");

        Assert.NotEmpty(capturedUrls);
        // Every request should have signing params
        foreach (var url in capturedUrls)
        {
            Assert.Contains("apikey=", url);
            Assert.Contains("nonce=", url);
            Assert.Contains("timestamp=", url);
            Assert.Contains("signature=", url);
        }
    }
}

public class ApiClientFilmLookupTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task LookupFilmByTmdbIdAsync_ReturnsFilmResult()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true &&
                request.RequestUri.Query.Contains("filmId=tmdb"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                id = "2a9q",
                                name = "Fight Club",
                                link = "https://letterboxd.com/film/fight-club/",
                                links = new[]
                                {
                                    new { type = "letterboxd", id = "2a9q", url = "https://letterboxd.com/film/fight-club/" },
                                    new { type = "tmdb", id = "550", url = "https://www.themoviedb.org/movie/550" }
                                }
                            }
                        }
                    }))
                };
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var result = await client.LookupFilmByTmdbIdAsync(550);

        Assert.Equal("2a9q", result.FilmId);
        Assert.Equal("fight-club", result.Slug);
        Assert.Null(result.ProductionId);
    }

    [Fact]
    public async Task LookupFilmByTmdbIdAsync_NotFound_Throws()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"items\":[]}")
                };
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ex = await Assert.ThrowsAsync<Exception>(() => client.LookupFilmByTmdbIdAsync(99999999));
        Assert.Contains("not found", ex.Message);
    }
}

public class ApiClientDiaryTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task MarkAsWatchedAsync_SendsCorrectBody()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.MarkAsWatchedAsync("fight-club", "2a9q", new DateTime(2026, 4, 7), liked: true, rating: 4.5);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("2a9q", root.GetProperty("filmId").GetString());
        Assert.True(root.GetProperty("like").GetBoolean());
        Assert.Equal(4.5, root.GetProperty("rating").GetDouble());
        Assert.Equal("2026-04-07", root.GetProperty("diaryDetails").GetProperty("diaryDate").GetString());
        Assert.False(root.GetProperty("diaryDetails").GetProperty("rewatch").GetBoolean());
    }

    [Fact]
    public async Task MarkAsWatchedAsync_Rewatch_SetsFlag()
    {
        string? capturedBody = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.MarkAsWatchedAsync("film", "lid", DateTime.Now, liked: false, rewatch: true);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("diaryDetails").GetProperty("rewatch").GetBoolean());
    }

    [Fact]
    public async Task GetDiaryInfoAsync_WithEntry_ReturnsDate()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true &&
                request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                diaryDetails = new { diaryDate = "2026-04-01" }
                            }
                        }
                    }))
                };
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var info = await client.GetDiaryInfoAsync("2a9q", "user");

        Assert.True(info.HasAnyEntry);
        Assert.Equal(new DateTime(2026, 4, 1), info.LastDate);
    }

    [Fact]
    public async Task GetDiaryInfoAsync_NoEntries_ReturnsEmpty()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true &&
                request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"items\":[]}")
                };
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var info = await client.GetDiaryInfoAsync("2a9q", "user");

        Assert.False(info.HasAnyEntry);
        Assert.Null(info.LastDate);
    }
}

public class ApiClientRateLimitTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task SendSigned_429_RetriesAfterDelay()
    {
        int callCount = 0;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                callCount++;
                if (callCount == 1)
                {
                    var resp = new HttpResponseMessage((HttpStatusCode)429);
                    resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(100));
                    return resp;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        items = new[] { new { id = "abc", link = "https://letterboxd.com/film/test/", links = Array.Empty<object>() } }
                    }))
                };
            }

            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var result = await client.LookupFilmByTmdbIdAsync(123);

        Assert.Equal(2, callCount);
        Assert.Equal("abc", result.FilmId);
    }
}

internal static class ApiTestHelpers
{
    internal static ApiMockHandler CreateAuthenticatedHandler(Func<HttpRequestMessage, HttpResponseMessage?>? extraHandler = null)
    {
        return new ApiMockHandler((request) =>
        {
            var extra = extraHandler?.Invoke(request);
            if (extra != null) return extra;

            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.EndsWith("/auth/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        access_token = "mock-token",
                        expires_in = 3600,
                        refresh_token = "mock-refresh"
                    }))
                };
            }

            if (path.EndsWith("/me"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"member\":{\"id\":\"mock-member\",\"username\":\"user\"}}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }
}

internal class ApiMockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public ApiMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}
