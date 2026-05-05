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
}
