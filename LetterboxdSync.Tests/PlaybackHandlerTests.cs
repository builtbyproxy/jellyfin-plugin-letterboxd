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

        var handler = new MockHandler((request, client) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // CSRF refresh — inject cookie into the container
            if (request.Method == HttpMethod.Get && path == "/")
            {
                client._cookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "testtoken"));
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
                client._cookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "newSession"));
                client._cookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "newcsrf"));
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

        using var client = handler.CreateClient(TestLogger);

        // Set stale cookies so HasAuthenticatedSession() returns true initially
        client.SetRawCookies("letterboxd.user.CURRENT=staleSession; com.xk72.webparts.csrf=stalecsrf");
        await client.AuthenticateAsync("testuser", "testpass");

        // Should succeed after auto re-auth
        await client.MarkAsWatchedAsync("test-film", "12345", DateTime.Now, false, "PROD1");

        Assert.True(logEntryCallCount >= 2, $"Expected at least 2 calls to production-log-entries, got {logEntryCallCount}");
        Assert.True(didLogin, "Expected a fresh login after 401");
    }

    [Fact]
    public async Task ForceReauthenticate_ClearsSessionAndLogs()
    {
        int loginCount = 0;

        var handler = new MockHandler((request, client) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                client._cookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "testtoken"));
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
                client._cookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "freshSession"));
                client._cookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "freshcsrf"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = handler.CreateClient(TestLogger);
        client.SetRawCookies("letterboxd.user.CURRENT=staleSession; com.xk72.webparts.csrf=stalecsrf");

        await client.AuthenticateAsync("testuser", "testpass");

        // First auth should reuse session (no login call)
        Assert.Equal(0, loginCount);

        // Force re-auth should do a fresh login
        await client.ForceReauthenticateAsync("testuser", "testpass");

        Assert.Equal(1, loginCount);
    }

    /// <summary>
    /// Mock handler that receives the LetterboxdClient so it can manipulate cookies directly.
    /// </summary>
    private class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdClient, HttpResponseMessage> _responder;
        private LetterboxdClient? _client;

        public MockHandler(Func<HttpRequestMessage, LetterboxdClient, HttpResponseMessage> responder)
            => _responder = responder;

        public LetterboxdClient CreateClient(ILogger logger)
        {
            var client = new LetterboxdClient(logger, this);
            _client = client;
            return client;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _client!));
    }
}
