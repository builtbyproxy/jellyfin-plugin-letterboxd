using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Smoke-tests for the IScheduledTask wrappers. These delegate straight to their
/// runners, but the metadata (Name, Key, Category, default triggers) is what
/// Jellyfin's task scheduler displays and uses, so we lock it in here.
/// </summary>
[Collection("Plugin")]
public class ScheduledTaskTests : IDisposable
{
    private readonly string _tempDir;

    public ScheduledTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-tasks-" + Guid.NewGuid().ToString("N"));
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
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private LetterboxdSyncRunner MakeSyncRunner(IUserManager um, ILibraryManager lm, IUserDataManager udm)
        => new(NullLoggerFactory.Instance, lm, um, udm);

    private WatchlistSyncRunner MakeWatchlistRunner(IUserManager um, ILibraryManager lm, IPlaylistManager pm)
        => new(NullLoggerFactory.Instance, lm, um, pm);

    [Fact]
    public void SyncTask_Metadata()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        Assert.Equal("Sync watched movies to Letterboxd", task.Name);
        Assert.Equal("LetterboxdSync", task.Key);
        Assert.Equal("Letterboxd", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void SyncTask_DefaultTrigger_IsDaily()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[0].Type);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, triggers[0].IntervalTicks);
    }

    [Fact]
    public async Task SyncTask_ExecuteAsync_DelegatesToRunner()
    {
        // We can't easily Substitute a concrete LetterboxdSyncRunner, so we use
        // a real one with empty users — the task just calls RunForAllAsync.
        var um = Substitute.For<IUserManager>();
        um.Users.Returns(Array.Empty<Jellyfin.Database.Implementations.Entities.User>());
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        // No exception = pass; runner exited cleanly with no users to process.
    }

    [Fact]
    public void WatchlistSyncTask_Metadata()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        Assert.Equal("Sync Letterboxd watchlist to playlist", task.Name);
        Assert.Equal("LetterboxdWatchlistSync", task.Key);
        Assert.Equal("Letterboxd", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void WatchlistSyncTask_DefaultTrigger_IsDaily()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Single(triggers);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, triggers[0].IntervalTicks);
    }

    [Fact]
    public async Task WatchlistSyncTask_ExecuteAsync_DelegatesToRunner()
    {
        var um = Substitute.For<IUserManager>();
        um.Users.Returns(Array.Empty<Jellyfin.Database.Implementations.Entities.User>());
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
    }
}
