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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class DiaryImportTaskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DiaryImportTask _task;

    public DiaryImportTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-diary-" + Guid.NewGuid().ToString("N"));
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
        _userDataManager = Substitute.For<IUserDataManager>();

        _task = new DiaryImportTask(_userManager, NullLoggerFactory.Instance, _libraryManager, _userDataManager);
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// Constructs a real User entity. NSubstitute can't proxy User because it has
    /// no parameterless constructor; the auto-generated Id is fine for our tests
    /// since we derive the JF user-id string from it via ToString("N").
    /// </summary>
    private static (User User, string IdHex) MakeUser(string name)
    {
        var u = new User(name, "test-provider-id", "test-reset-id");
        return (u, u.Id.ToString("N"));
    }

    /// <summary>
    /// GetProviderId is an extension method that reads from ProviderIds, so we can't
    /// substitute it. Construct a real Movie and seed its ProviderIds dictionary
    /// directly; SetProviderId is the public extension that does this.
    /// </summary>
    private static Movie MakeMovie(int tmdbId, string name = "Sinners")
    {
        var movie = new Movie { Name = name };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return movie;
    }

    private static UserItemData MakeUserData(bool played = false, double? rating = null)
    {
        // UserItemData has a required Key property in Jellyfin 10.11; the value
        // doesn't matter for our assertions, only that the object is constructable.
        return new UserItemData { Key = "test-key", Played = played, Rating = rating };
    }

    [Fact]
    public async Task ExecuteAsync_NoUsers_DoesNothing()
    {
        _userManager.Users.Returns(new List<User>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // No factory call made, no userdata saves; nothing to assert beyond not throwing.
        Assert.False(LetterboxdSyncRunner.IsRunning);
    }

    [Fact]
    public async Task ExecuteAsync_UserHasNoAccount_SkippedSilently()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // No factory override needed; we never get to it.
        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task ExecuteAsync_AccountWithoutDiaryImportFlag_Skipped()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "u",
            Enabled = true,
            EnableDiaryImport = false  // The flag the task filters on.
        });

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task ExecuteAsync_AuthFails_SkipsUserButContinues()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "u",
            Enabled = true,
            EnableDiaryImport = true
        });

        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new Exception("auth failed");

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // No library queries because we never got past auth.
        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task ExecuteAsync_FetchDiaryFails_SkipsUser()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "u",
            Enabled = true,
            EnableDiaryImport = true
        });

        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns<Task<List<DiaryFilmEntry>>>(_ => throw new Exception("403"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDiary_DoesNotQueryLibrary()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "u",
            Enabled = true,
            EnableDiaryImport = true
        });

        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>()).Returns(new List<DiaryFilmEntry>());
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public async Task ExecuteAsync_DiaryEntriesMatched_MarksMovieAsPlayed()
    {
        var (user, userId) = MakeUser("lachlan");
        // (already declared above)
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId, LetterboxdUsername = "u",
            Enabled = true, EnableDiaryImport = true
        });

        // Letterboxd diary has TMDb 1233413; library has the same movie unplayed.
        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns(new List<DiaryFilmEntry> { new(1233413, null) });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413, "Sinners");
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie });

        var userData = MakeUserData(played: false, rating: null);
        _userDataManager.GetUserData(user, movie).Returns(userData);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(userData.Played);
        _userDataManager.Received(1).SaveUserData(
            user, movie, userData, UserDataSaveReason.Import, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RatedDiaryEntry_AppliesRatingWhenJellyfinHasNone()
    {
        var (user, userId) = MakeUser("lachlan");
        // (already declared above)
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId, LetterboxdUsername = "u",
            Enabled = true, EnableDiaryImport = true
        });

        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns(new List<DiaryFilmEntry> { new(1233413, 4.5) });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });

        // JF rating is null, so the LB rating should be applied.
        var userData = MakeUserData(played: true, rating: null);
        _userDataManager.GetUserData(user, movie).Returns(userData);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(9.0, userData.Rating); // LB 4.5 → JF 9.0
        _userDataManager.Received(1).SaveUserData(user, movie, userData,
            UserDataSaveReason.Import, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RatedDiaryEntry_DoesNotOverwriteExistingJellyfinRating()
    {
        var (user, userId) = MakeUser("lachlan");
        // (already declared above)
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId, LetterboxdUsername = "u",
            Enabled = true, EnableDiaryImport = true
        });

        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns(new List<DiaryFilmEntry> { new(1233413, 3.0) });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });

        // JF already has rating 8.0 (e.g. set in Findroid). Don't clobber.
        var userData = MakeUserData(played: true, rating: 8.0);
        _userDataManager.GetUserData(user, movie).Returns(userData);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(8.0, userData.Rating); // unchanged
        // No save call because nothing changed.
        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyPlayedNoRating_NoSaveCall()
    {
        var (user, userId) = MakeUser("lachlan");
        // (already declared above)
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId, LetterboxdUsername = "u",
            Enabled = true, EnableDiaryImport = true
        });

        // Diary has the film but no rating; library already marked as played.
        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns(new List<DiaryFilmEntry> { new(1233413, null) });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var movie = MakeMovie(1233413);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });

        var userData = MakeUserData(played: true, rating: null);
        _userDataManager.GetUserData(user, movie).Returns(userData);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Nothing to do: already played, no rating to apply. No save.
        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FilmNotInLibrary_RatingDropped()
    {
        var (user, userId) = MakeUser("lachlan");
        // (already declared above)
        _userManager.Users.Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId, LetterboxdUsername = "u",
            Enabled = true, EnableDiaryImport = true
        });

        // Diary mentions a film, but library has nothing.
        var service = Substitute.For<ILetterboxdService>();
        service.GetDiaryFilmEntriesAsync(Arg.Any<string>())
            .Returns(new List<DiaryFilmEntry> { new(1233413, 4.0) });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(),
            Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDefaultTriggers_ReturnsDailyInterval()
    {
        var triggers = _task.GetDefaultTriggers().ToList();

        Assert.Single(triggers);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, triggers[0].IntervalTicks);
    }

    [Fact]
    public void Metadata_NameKeyDescriptionCategory()
    {
        // Pinned strings, since these show up in the Jellyfin Scheduled Tasks UI.
        Assert.Equal("Import Letterboxd diary to Jellyfin", _task.Name);
        Assert.Equal("LetterboxdDiaryImport", _task.Key);
        Assert.Equal("Letterboxd", _task.Category);
        Assert.False(string.IsNullOrEmpty(_task.Description));
    }
}
