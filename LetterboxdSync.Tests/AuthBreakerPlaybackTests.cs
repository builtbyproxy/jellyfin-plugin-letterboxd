using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Auth breaker behavior through the real-time playback path. The handler here
/// is constructed WITHOUT an IActivityManager (the optional-parameter default),
/// so these tests also prove the breaker works when the notification sink is
/// absent: it opens, skips, and resets identically, just without the
/// activity-log entry.
/// </summary>
[Collection("Plugin")]
public class AuthBreakerPlaybackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PlaybackHandler _handler;

    public AuthBreakerPlaybackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-pb-brk-" + Guid.NewGuid().ToString("N"));
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

        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = Path.Combine(_tempDir, "auth-breaker.json");
        AuthBreaker.ResetForTesting();

        _handler = new PlaybackHandler(
            Substitute.For<ISessionManager>(),
            Substitute.For<IUserDataManager>(),
            new LoggerFactory().CreateLogger<PlaybackHandler>());
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private (User User, string IdHex) SetUpUserWithAccount()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "lb-user",
            LetterboxdPassword = "secret",
            Enabled = true
        });
        return (user, userId);
    }

    private static Movie MakeMovie()
    {
        var movie = new Movie { Name = "Sinners" };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");
        return movie;
    }

    private Task Play(User user) => _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
    {
        Item = MakeMovie(),
        PlayedToCompletion = true,
        Users = new List<User> { user }
    });

    [Fact]
    public async Task OpenBreaker_SkipsWithoutFactoryCall_AndRecordsSkipInHistory()
    {
        var (user, userId) = SetUpUserWithAccount();
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure(userId, "lb-user", "bad password");

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ILetterboxdService>()); };

        await Play(user);

        Assert.False(factoryHit);
        var page = SyncHistory.GetPage(0, 10, "lachlan");
        var skip = Assert.Single(page.Events.Where(ev => ev.Status == SyncStatus.Skipped));
        Assert.Contains("paused", skip.Error);
    }

    [Fact]
    public async Task ThreeAuthFailures_OpenBreaker_WithNullActivityManager()
    {
        var (user, userId) = SetUpUserWithAccount();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new InvalidOperationException("Login failed");

        for (var i = 0; i < 3; i++) await Play(user);

        // No IActivityManager was provided; the breaker must still open cleanly.
        Assert.True(AuthBreaker.IsOpen(userId, "lb-user"));
    }

    [Fact]
    public async Task PostAuthError_DoesNotFeedBreaker()
    {
        var (user, userId) = SetUpUserWithAccount();

        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns<Task<FilmResult>>(_ => throw new InvalidOperationException("lookup blew up after auth"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        for (var i = 0; i < 4; i++) await Play(user);

        // Four post-auth failures: the breaker must be untouched (auth succeeded each time).
        Assert.False(AuthBreaker.IsOpen(userId, "lb-user"));
        Assert.Null(AuthBreaker.GetState(userId, "lb-user"));
    }
}
