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

public class AuthTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    [Fact]
    public async Task AuthenticateAsync_FreshLogin_PostsCredentials()
    {
        bool loginPosted = false;
        string? postedUsername = null;

        var handler = new AuthMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "fresh-csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<input type=\"hidden\" name=\"__csrf\" value=\"form-csrf\" />")
                };
            }

            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                loginPosted = true;
                var content = request.Content?.ReadAsStringAsync().Result ?? "";
                if (content.Contains("username=testuser"))
                    postedUsername = "testuser";

                http.CookieContainer.Add(BaseUri, new Cookie("letterboxd.user.CURRENT", "new-session"));
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "post-login-csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth) = handler.CreateClients(TestLogger);
        using var _ = http;

        await auth.AuthenticateAsync("testuser", "testpass");

        Assert.True(loginPosted, "Expected login POST");
        Assert.Equal("testuser", postedUsername);
        Assert.Equal("testuser", auth.Username);
    }

    [Fact]
    public async Task AuthenticateAsync_ExistingSession_ReusesWithoutLogin()
    {
        int loginCount = 0;

        var handler = new AuthMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                loginCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"success\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth) = handler.CreateClients(TestLogger);
        using var _ = http;

        // Pre-set session cookie
        http.SetRawCookies("letterboxd.user.CURRENT=existing-session; com.xk72.webparts.csrf=existing-csrf");

        await auth.AuthenticateAsync("testuser", "testpass");

        Assert.Equal(0, loginCount);
    }

    [Fact]
    public async Task AuthenticateAsync_LoginError_Throws()
    {
        var handler = new AuthMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<input type=\"hidden\" name=\"__csrf\" value=\"csrf\" />")
                };
            }

            if (request.Method == HttpMethod.Post && path.Contains("login.do"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":\"error\",\"messages\":[\"Invalid password\"]}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ex = await Assert.ThrowsAsync<Exception>(
            () => auth.AuthenticateAsync("testuser", "wrongpass"));

        Assert.Contains("Invalid password", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_403OnSignIn_ThrowsCloudflareError()
    {
        var handler = new AuthMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (request.Method == HttpMethod.Get && path == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/sign-in"))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, auth) = handler.CreateClients(TestLogger);
        using var _ = http;

        var ex = await Assert.ThrowsAsync<Exception>(
            () => auth.AuthenticateAsync("testuser", "testpass"));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public void ShouldReauthenticate_FirstCall_ReturnsTrue()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        var auth = new LetterboxdAuth(http, TestLogger);

        Assert.True(auth.ShouldReauthenticate());
    }

    [Fact]
    public void ShouldReauthenticate_SecondCall_ReturnsFalse()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        var auth = new LetterboxdAuth(http, TestLogger);

        auth.ShouldReauthenticate();
        Assert.False(auth.ShouldReauthenticate());
    }

    [Fact]
    public void ResetReauthGuard_AllowsReauth()
    {
        using var http = new LetterboxdHttpClient(TestLogger);
        var auth = new LetterboxdAuth(http, TestLogger);

        auth.ShouldReauthenticate();
        Assert.False(auth.ShouldReauthenticate());

        auth.ResetReauthGuard();
        Assert.True(auth.ShouldReauthenticate());
    }

    internal class AuthMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public AuthMockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
            => _responder = responder;

        public (LetterboxdHttpClient Http, LetterboxdAuth Auth) CreateClients(ILogger logger)
        {
            var http = new LetterboxdHttpClient(logger, this);
            _httpClient = http;
            var auth = new LetterboxdAuth(http, logger);
            return (http, auth);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _httpClient!));
    }
}
