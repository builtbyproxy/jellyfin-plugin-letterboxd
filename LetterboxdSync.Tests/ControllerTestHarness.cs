using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LetterboxdSync.Tests;

/// <summary>
/// Builds a controller wired to a real Plugin singleton (for Config access) and
/// substituted Jellyfin services. Each test gets a fresh temp directory and a
/// fresh Plugin instance, so configuration writes don't bleed between tests.
/// </summary>
internal sealed class ControllerTestHarness : IDisposable
{
    public LetterboxdController Controller { get; }
    public IUserManager UserManager { get; }
    public ILibraryManager LibraryManager { get; }
    public IUserDataManager UserDataManager { get; }
    public IApplicationPaths AppPaths { get; }
    public LetterboxdSyncRunner SyncRunner { get; }
    public WatchlistSyncRunner WatchlistRunner { get; }
    public PluginConfiguration Config => Plugin.Instance!.Configuration;
    public string TempDir { get; }
    public string LogDir { get; }

    private static readonly object _pluginLock = new();

    public ControllerTestHarness(string? currentUserId = null)
    {
        TempDir = Path.Combine(Path.GetTempPath(), "lbs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        LogDir = Path.Combine(TempDir, "log");
        Directory.CreateDirectory(LogDir);

        // IApplicationPaths is required by both Plugin (for config persistence) and
        // the controller (for log file lookup). Point everything at the harness's
        // temp directory so file writes are isolated and harmless.
        AppPaths = Substitute.For<IApplicationPaths>();
        AppPaths.PluginConfigurationsPath.Returns(TempDir);
        AppPaths.LogDirectoryPath.Returns(LogDir);
        AppPaths.DataPath.Returns(TempDir);
        AppPaths.CachePath.Returns(TempDir);

        // IXmlSerializer is what BasePlugin uses to load/save the plugin config XML.
        // We let the substitute return null on deserialise so Plugin uses defaults,
        // and ignore writes (we manipulate Config in-process instead).
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());

        // Plugin.Instance is a singleton; tests must serialise their setup so one
        // doesn't observe another's Config. The lock here, combined with disposing
        // and rebuilding per test, keeps state from leaking across the suite.
        lock (_pluginLock)
        {
            // Force a fresh Plugin every harness; the constructor sets Plugin.Instance.
            new Plugin(AppPaths, xml);
        }

        UserManager = Substitute.For<IUserManager>();
        LibraryManager = Substitute.For<ILibraryManager>();
        UserDataManager = Substitute.For<IUserDataManager>();

        var loggerFactory = NullLoggerFactory.Instance;
        SyncRunner = new LetterboxdSyncRunner(loggerFactory, LibraryManager, UserManager, UserDataManager);

        // WatchlistSyncRunner needs IPlaylistManager too. We're not testing the
        // runner directly; the controller only invokes TryRunForUserAsync as a
        // fire-and-forget Task.Run, so this constructor just needs to succeed.
        var playlistManager = Substitute.For<IPlaylistManager>();
        WatchlistRunner = new WatchlistSyncRunner(loggerFactory, LibraryManager, UserManager, playlistManager);

        Controller = new LetterboxdController(
            NullLoggerFactory.Instance.CreateLogger<LetterboxdController>(),
            UserManager,
            LibraryManager,
            UserDataManager,
            AppPaths,
            SyncRunner,
            WatchlistRunner);

        // GetCurrentUserId reads the Jellyfin-UserId claim from User. We feed a
        // claims principal into ControllerContext so the controller's helpers see
        // the right user id (or no user, when null is passed in).
        var principal = currentUserId == null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("Jellyfin-UserId", currentUserId)
                }, "Test"));

        var httpContext = new DefaultHttpContext { User = principal };
        Controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData()
        };
    }

    /// <summary>
    /// Adds an enabled Account to the in-memory plugin configuration for the given
    /// Jellyfin user id, returning the account so tests can tweak per-feature flags.
    /// </summary>
    public Account AddAccount(string userId, string lbUsername, bool enabled = true, bool watchlistSync = false)
    {
        var account = new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = lbUsername,
            LetterboxdPassword = "secret",
            Enabled = enabled,
            EnableWatchlistSync = watchlistSync
        };
        Config.Accounts.Add(account);
        return account;
    }

    /// <summary>
    /// Stubs IUserManager.Users with the supplied user list so the controller's
    /// GetJellyfinUsername helper can resolve a user id back to a Username.
    /// </summary>
    public void SetUsers(params (string Id, string Name)[] users)
    {
        var list = new List<User>();
        foreach (var (id, name) in users)
        {
            var u = Substitute.For<User>();
            // User.Id is a Guid; the controller compares by id.ToString("N").
            // Build a Guid that matches the no-dash hex string the test passes in.
            var guid = Guid.ParseExact(id.Replace("-", ""), "N");
            u.Id.Returns(guid);
            u.Username.Returns(name);
            list.Add(u);
        }
        UserManager.Users.Returns(list);
    }

    /// <summary>
    /// Adds a Movie to the library mock so the controller's library queries
    /// (used by WriteJellyfinRating writeback) can find it by TMDb id.
    /// </summary>
    public Movie AddMovie(int tmdbId, string name = "Sinners")
    {
        var movie = Substitute.For<Movie>();
        movie.Name.Returns(name);
        movie.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb).Returns(tmdbId.ToString());
        // GetItemList returns a list of all movies; we just always return this set.
        // Controller queries by-user but we don't differentiate per-user here.
        var existing = LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Cast<BaseItem>().ToList();
        existing.Add(movie);
        LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(existing);
        return movie;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}
