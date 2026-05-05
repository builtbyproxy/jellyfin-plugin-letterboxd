using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Drives PlaybackHandler.HandlePlaybackStoppedAsync directly (it's internal so tests
/// can skip the event-raise machinery on ISessionManager). The real-time playback
/// path is the most user-visible sync code; the early-exit branches especially are
/// where we've historically had bugs (TMDb-less items, non-movie items, partial plays).
/// </summary>
[Collection("Plugin")]
public class PlaybackHandlerEarlyExitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly PlaybackHandler _handler;

    public PlaybackHandlerEarlyExitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-pb-" + Guid.NewGuid().ToString("N"));
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

        _sessionManager = Substitute.For<ISessionManager>();
        _userDataManager = Substitute.For<IUserDataManager>();
        _handler = new PlaybackHandler(_sessionManager, _userDataManager,
            new LoggerFactory().CreateLogger<PlaybackHandler>());
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

    private static Movie MakeMovie(int? tmdbId = 1233413, string name = "Sinners")
    {
        var movie = new Movie { Name = name };
        if (tmdbId.HasValue)
            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.Value.ToString());
        return movie;
    }

    private void AddAccount(string userId, bool enabled = true)
    {
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "lb-user",
            LetterboxdPassword = "secret",
            Enabled = enabled
        });
    }

    [Fact]
    public async Task NullItem_NoSyncAttempted()
    {
        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs { Item = null });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task NonMovieItem_NoSyncAttempted()
    {
        var (user, _) = MakeUser("lachlan");
        // Episodes shouldn't trigger sync; only movies are in scope.
        var episode = new Episode { Name = "S01E01" };

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = episode,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task NotPlayedToCompletion_NoSyncAttempted()
    {
        var (user, _) = MakeUser("lachlan");
        var movie = MakeMovie();

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = false,  // 50% played, didn't finish
            Users = new List<User> { user }
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task NoUsers_NoSyncAttempted()
    {
        var movie = MakeMovie();

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User>()
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task UserWithoutAccount_NoSyncForThatUser()
    {
        var (user, _) = MakeUser("lachlan");  // no account configured for this user
        var movie = MakeMovie();

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task DisabledAccount_NoSync()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId, enabled: false);
        var movie = MakeMovie();

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task MovieWithoutTmdbId_SkippedWithWarning_NoFactoryCall()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId);
        var movie = MakeMovie(tmdbId: null);  // no TMDb id

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task AuthFails_RecordsFailedSyncEvent()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId);
        var movie = MakeMovie();

        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new Exception("auth failed");

        // Handler swallows the exception (catch wraps the per-user block); test
        // asserts that no exception escapes and we don't crash the event handler.
        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });
    }

    [Fact]
    public async Task DuplicateOnSameDay_RecordsSkipNotSync()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId);
        var movie = MakeMovie();

        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(new FilmResult("sinners-2025", "KQMM", "PROD-1"));
        // Diary already has an entry for today -> IsDuplicate returns true.
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new DiaryInfo(DateTime.Now.Date, true));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        // Duplicate path returns before MarkAsWatchedAsync, so it's never called.
        await service.DidNotReceive().MarkAsWatchedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<double?>());
    }

    [Fact]
    public async Task FreshWatch_CallsMarkAsWatched()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId);
        var movie = MakeMovie();

        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(new FilmResult("sinners-2025", "KQMM", "PROD-1"));
        // No existing diary entry.
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new DiaryInfo(null, false));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        await service.Received(1).MarkAsWatchedAsync(
            "sinners-2025", "KQMM", Arg.Any<DateTime?>(), Arg.Any<bool>(),
            "PROD-1", false /*not a rewatch*/, Arg.Any<double?>());
    }

    [Fact]
    public async Task FreshWatch_PassesRatingFromUserData()
    {
        var (user, userId) = MakeUser("lachlan");
        AddAccount(userId);
        var movie = MakeMovie();

        // userData has rating=8 (Jellyfin scale) → should map to LB 4.0
        _userDataManager.GetUserData(user, movie).Returns(new UserItemData
        {
            Key = "k", Played = true, Rating = 8.0
        });

        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(new FilmResult("sinners-2025", "KQMM", "PROD-1"));
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new DiaryInfo(null, false));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        double? capturedRating = null;
        service.MarkAsWatchedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Do<double?>(r => capturedRating = r));

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        Assert.Equal(4.0, capturedRating);
    }
}
