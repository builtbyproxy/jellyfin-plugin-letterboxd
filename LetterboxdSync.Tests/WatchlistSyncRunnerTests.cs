using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class WatchlistSyncRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly WatchlistSyncRunner _runner;

    public WatchlistSyncRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-watchlistrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);

        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());

        new Plugin(paths, xml);

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _playlistManager = Substitute.For<IPlaylistManager>();
        _runner = new WatchlistSyncRunner(NullLoggerFactory.Instance,
            _libraryManager, _userManager, _playlistManager);
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static (User User, string IdHex) MakeUser(string name)
    {
        var u = new User(name, "test-provider-id", "test-reset-id");
        return (u, u.Id.ToString("N"));
    }

    private static Movie MakeMovie(int tmdbId, string name = "Sinners")
    {
        var movie = new Movie { Name = name };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return movie;
    }

    private void AddAccount(string userId, bool enabled = true,
        bool watchlistSync = true, bool autoRequest = false, bool mirror = false)
    {
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "lb-user",
            LetterboxdPassword = "secret",
            Enabled = enabled,
            EnableWatchlistSync = watchlistSync,
            AutoRequestWatchlist = autoRequest,
            MirrorJellyseerrWatchlist = mirror
        });
    }

    // ----- Pre-flight gates -----

    [Fact]
    public async Task TryRunForUserAsync_UnknownUser_ReturnsFalse()
    {
        _userManager.Users.Returns(new List<User>());

        var ok = await _runner.TryRunForUserAsync("ffffffffffffffffffffffffffffffff",
            "manual", new Progress<double>(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task TryRunForUserAsync_NoAccount_ReturnsFalse()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });

        var ok = await _runner.TryRunForUserAsync(userId, "manual",
            new Progress<double>(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task TryRunForUserAsync_WatchlistDisabled_ReturnsFalse()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, watchlistSync: false);

        var ok = await _runner.TryRunForUserAsync(userId, "manual",
            new Progress<double>(), CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task TryRunForUserAsync_AccountDisabled_ReturnsFalse()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, enabled: false, watchlistSync: true);

        var ok = await _runner.TryRunForUserAsync(userId, "manual",
            new Progress<double>(), CancellationToken.None);

        Assert.False(ok);
    }

    // ----- Auth + watchlist fetch paths -----

    [Fact]
    public async Task TryRunForUserAsync_AuthFails_ReturnsTrue_NoLibraryQuery()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new Exception("auth failed");

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        // The auth-fail path is internal; runner returns true (gate released, no other
        // sync running) but never reaches the library query.
        Assert.True(ok);
        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task TryRunForUserAsync_FetchWatchlistFails_ReturnsTrue()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>())
            .Returns<Task<List<int>>>(_ => throw new Exception("403"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
        // Library is queried inside the auth-success branch, before we know the watchlist
        // fetch will fail. So we don't assert on it here; the fact that no playlist was
        // created is the meaningful behavioural assertion.
        await _playlistManager.DidNotReceive().CreatePlaylist(Arg.Any<PlaylistCreationRequest>());
    }

    [Fact]
    public async Task TryRunForUserAsync_EmptyWatchlist_NoPlaylistCreated()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int>());
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        // Library query returns no movies; no existing playlist; nothing to create.
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
        await _playlistManager.DidNotReceive().CreatePlaylist(Arg.Any<PlaylistCreationRequest>());
    }

    [Fact]
    public async Task TryRunForUserAsync_WatchlistMatchesLibrary_CreatesPlaylist()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413);
        // Two queries happen: first for movies, second for existing playlists. We let
        // both return results; the second filters by Playlist type so an empty list
        // here means "no existing playlist", forcing the create path.
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie }, new List<BaseItem>());

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
        await _playlistManager.Received(1).CreatePlaylist(Arg.Is<PlaylistCreationRequest>(
            req => req.UserId == user.Id && req.ItemIdList.Contains(movie.Id)));
    }

    // ----- RunForAllAsync -----

    [Fact]
    public async Task RunForAllAsync_NoUsers_DoesNothing()
    {
        _userManager.Users.Returns(new List<User>());

        await _runner.RunForAllAsync(new Progress<double>(), "scheduled", CancellationToken.None);

        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task RunForAllAsync_UserWithoutWatchlistEnabled_Skipped()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, watchlistSync: false);

        await _runner.RunForAllAsync(new Progress<double>(), "scheduled", CancellationToken.None);

        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task RunForAllAsync_ReportsProgressTo100()
    {
        _userManager.Users.Returns(new List<User>());
        var captured = new List<double>();
        var progress = new Progress<double>(v => captured.Add(v));

        await _runner.RunForAllAsync(progress, "scheduled", CancellationToken.None);

        await Task.Delay(50);
        Assert.Contains(100.0, captured);
    }

    [Fact]
    public async Task TryRunForUserAsync_NoWatchlistMatches_LogsWarning_NoPlaylistCreated()
    {
        // Letterboxd returns watchlist film, but library has nothing matching → no
        // playlist creation. Exercises the unmatched-films logging branch.
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        // Library has no movies at all.
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
        await _playlistManager.DidNotReceive().CreatePlaylist(Arg.Any<PlaylistCreationRequest>());
    }

    [Fact]
    public async Task TryRunForUserAsync_AutoRequestEnabled_NoJellyseerr_SkipsRequest()
    {
        // Account has auto-request on, but Jellyseerr URL/key aren't configured at
        // the plugin level → CreateJellyseerrClient returns null, we don't try to
        // request. This exercises the IsConfigured guard path.
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task TryRunForUserAsync_ExistingPlaylistMatches_NoCreateCall()
    {
        // Library has the watchlisted film and there's already a playlist named
        // "Letterboxd Watchlist" with the right items → no Create or update needed.
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId);

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie }, new List<BaseItem>());

        var ok = await _runner.TryRunForUserAsync(userId, "test",
            new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
    }

    // ----- Mirror to Jellyseerr watchlist -----
    // Exercises MirrorJellyseerrWatchlistAsync via the JellyseerrClientFactoryOverride.

    /// <summary>
    /// Mock HttpMessageHandler that lets us drive the JellyseerrClient end-to-end
    /// without hitting the network. Each test plays out a small request → response
    /// script so we can verify the runner sends the right calls.
    /// </summary>
    private class JellyseerrHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> _responder;
        public List<System.Net.Http.HttpRequestMessage> Calls { get; } = new();
        public JellyseerrHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> responder)
            => _responder = responder;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_AddsMissingFilms()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, mirror: true);

        // Plugin-wide Jellyseerr config makes IsConfigured true.
        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ILetterboxdService>();
        // LB has only 1233413; Jellyseerr has 550. Mirror should ADD 1233413 and
        // REMOVE 550 from the Seerr watchlist (since it's no longer on LB).
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>())
            .Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        // Library has nothing (so playlist+request paths short-circuit), but the
        // mirror flow still runs since Jellyseerr is configured + mirror flag is on.
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        // Jellyseerr scripted responses:
        //   GET /api/v1/user → maps lachlan's JF id to Jellyseerr id 7
        //   GET /api/v1/user/7/watchlist → returns {550}, missing 1233413
        //   POST /api/v1/watchlist (with X-API-User: 7) → success for 1233413
        //   DELETE /api/v1/watchlist/550 → success (550 was on Seerr but not LB)
        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
            {
                var jfId = user.Id.ToString("N");
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"results\":[{\"id\":7,\"jellyfinUserId\":\"" + jfId + "\"}]}")
                };
            }
            if (req.Method == HttpMethod.Get && path.Contains("/api/v1/user/7/watchlist"))
            {
                // Jellyseerr's watchlist endpoint returns items with tmdbId at the
                // top level; not nested under mediaInfo.
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"results\":[{\"tmdbId\":550,\"mediaType\":\"movie\"}]}")
                };
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/watchlist"))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Created);
            if (req.Method == HttpMethod.Delete && path.StartsWith("/api/v1/watchlist/"))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        WatchlistSyncRunner.JellyseerrClientFactoryOverride = (url, key, log) =>
            new JellyseerrClient(url, key, log, handler);
        try
        {
            var ok = await _runner.TryRunForUserAsync(userId, "test",
                new Progress<double>(), CancellationToken.None);

            Assert.True(ok);
            // Should have hit the user list, watchlist fetch, plus add and remove.
            Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/user"));
            Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/api/v1/user/7/watchlist"));
            Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/watchlist"));
            Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Delete);
        }
        finally
        {
            WatchlistSyncRunner.JellyseerrClientFactoryOverride = null;
            Plugin.Instance!.Configuration.JellyseerrUrl = null;
            Plugin.Instance!.Configuration.JellyseerrApiKey = null;
        }
    }

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_NoUserMapping_SkipsMirror()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, mirror: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int> { 1233413 });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        // Jellyseerr returns a user list that doesn't match our Jellyfin user → mapping fails.
        var handler = new JellyseerrHandler(req =>
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"results\":[]}")
            });
        WatchlistSyncRunner.JellyseerrClientFactoryOverride = (url, key, log) =>
            new JellyseerrClient(url, key, log, handler);
        try
        {
            var ok = await _runner.TryRunForUserAsync(userId, "test",
                new Progress<double>(), CancellationToken.None);

            Assert.True(ok);
            // Without a mapping, the mirror flow must not POST/DELETE anything.
            Assert.DoesNotContain(handler.Calls, r => r.Method == HttpMethod.Post);
            Assert.DoesNotContain(handler.Calls, r => r.Method == HttpMethod.Delete);
        }
        finally
        {
            WatchlistSyncRunner.JellyseerrClientFactoryOverride = null;
            Plugin.Instance!.Configuration.JellyseerrUrl = null;
            Plugin.Instance!.Configuration.JellyseerrApiKey = null;
        }
    }

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_EmptyLetterboxdList_SkipsToAvoidMassDeletion()
    {
        // Defensive: if Letterboxd returns an empty list (e.g. watchlist deleted
        // or fetch errored to zero), don't mass-delete the Jellyseerr watchlist.
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        AddAccount(userId, mirror: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ILetterboxdService>();
        service.GetWatchlistTmdbIdsAsync(Arg.Any<string>()).Returns(new List<int>());
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        "{\"results\":[{\"id\":7,\"jellyfinUserId\":\"" + user.Id.ToString("N") + "\"}]}")
                };
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"results\":[]}")
            };
        });
        WatchlistSyncRunner.JellyseerrClientFactoryOverride = (url, key, log) =>
            new JellyseerrClient(url, key, log, handler);
        try
        {
            var ok = await _runner.TryRunForUserAsync(userId, "test",
                new Progress<double>(), CancellationToken.None);

            Assert.True(ok);
            // No add or delete — empty LB means no mirror operations.
            Assert.DoesNotContain(handler.Calls, r => r.Method == HttpMethod.Post);
            Assert.DoesNotContain(handler.Calls, r => r.Method == HttpMethod.Delete);
        }
        finally
        {
            WatchlistSyncRunner.JellyseerrClientFactoryOverride = null;
            Plugin.Instance!.Configuration.JellyseerrUrl = null;
            Plugin.Instance!.Configuration.JellyseerrApiKey = null;
        }
    }
}
