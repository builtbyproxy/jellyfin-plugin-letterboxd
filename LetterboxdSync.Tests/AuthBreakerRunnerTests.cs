using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Auth circuit breaker behavior through LetterboxdSyncRunner (issue #103):
/// an open breaker skips the account without touching the factory, the third
/// consecutive auth failure notifies the admin exactly once, and a successful
/// login clears the failure run.
/// </summary>
[Collection("Plugin")]
public class AuthBreakerRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IActivityManager _activityManager;
    private readonly LetterboxdSyncRunner _runner;

    public AuthBreakerRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-breaker-run-" + Guid.NewGuid().ToString("N"));
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

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _userDataManager = Substitute.For<IUserDataManager>();
        _activityManager = Substitute.For<IActivityManager>();
        _runner = new LetterboxdSyncRunner(NullLoggerFactory.Instance,
            _libraryManager, _userManager, _userDataManager, _activityManager);
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

    private (User User, string IdHex) SetUpUserWithPlayedMovie(string lbUsername = "kostadamus")
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });

        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = lbUsername,
            LetterboxdPassword = "pw",
            Enabled = true,
            SkipPreviouslySynced = false
        });

        var movie = new Movie { Name = "Sinners", Id = Guid.NewGuid() };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });
        _userDataManager.GetUserData(user, movie).Returns(new UserItemData
        {
            Key = "k",
            Played = true,
            LastPlayedDate = DateTime.UtcNow.AddHours(-1)
        });

        return (user, userId);
    }

    [Fact]
    public async Task OpenBreaker_SkipsAccountWithoutFactoryCall_AndRecordsSkip()
    {
        var (_, userId) = SetUpUserWithPlayedMovie();
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure(userId, "kostadamus", "bad password");
        Assert.True(AuthBreaker.IsOpen(userId, "kostadamus"));

        var factoryHit = false;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
        {
            factoryHit = true;
            return Task.FromResult(Substitute.For<ILetterboxdService>());
        };

        var ok = await _runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None);

        Assert.True(ok);
        Assert.False(factoryHit);
        var page = SyncHistory.GetPage(0, 10, "lachlan");
        Assert.Contains(page.Events, e => e.Status == SyncStatus.Skipped && e.FilmTitle.Contains("paused"));
    }

    [Fact]
    public async Task ThirdConsecutiveAuthFailure_NotifiesAdminExactlyOnce()
    {
        var (_, userId) = SetUpUserWithPlayedMovie();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new InvalidOperationException("Login failed: bad credentials");

        for (var i = 0; i < 4; i++)
        {
            // Run 4 attempts: the 4th is skipped by the open breaker, so it must
            // not attempt auth and must not notify again.
            await _runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None);
        }

        Assert.True(AuthBreaker.IsOpen(userId, "kostadamus"));
        Assert.Equal(3, AuthBreaker.GetState(userId, "kostadamus")!.ConsecutiveFailures);
        await _activityManager.Received(1).CreateAsync(
            Arg.Is<Jellyfin.Database.Implementations.Entities.ActivityLog>(a => a.Name.Contains("kostadamus")));
    }

    [Fact]
    public async Task SuccessfulAuth_ClearsPriorFailures()
    {
        var (_, userId) = SetUpUserWithPlayedMovie();
        AuthBreaker.RecordFailure(userId, "kostadamus", "e");
        AuthBreaker.RecordFailure(userId, "kostadamus", "e");

        // Auth succeeds; the film-level lookup throws so the run finishes quickly.
        // Post-auth errors must not count against the breaker.
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns<Task<FilmResult>>(_ => throw new InvalidOperationException("lookup failed"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None);

        Assert.False(AuthBreaker.IsOpen(userId, "kostadamus"));
        Assert.Null(AuthBreaker.GetState(userId, "kostadamus"));
        await _activityManager.DidNotReceive().CreateAsync(Arg.Any<Jellyfin.Database.Implementations.Entities.ActivityLog>());
    }
}
