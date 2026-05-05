using System;
using System.Linq;
using System.Reflection;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Endpoint-level tests for LetterboxdController. Uses ControllerTestHarness to
/// stand up Plugin.Instance and substitute Jellyfin services. Each test owns its
/// harness via using-disposal so configuration state never leaks between tests.
/// </summary>
[Collection("Plugin")]
public class LetterboxdControllerTests
{
    /// <summary>Reads an anonymous-object property off an OkObjectResult / BadRequestObjectResult.</summary>
    private static T? Prop<T>(IActionResult result, string name)
    {
        var value = result switch
        {
            OkObjectResult ok => ok.Value,
            BadRequestObjectResult bad => bad.Value,
            ObjectResult obj => obj.Value,
            _ => null
        };
        if (value == null) return default;
        var prop = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop == null ? default : (T?)prop.GetValue(value);
    }

    private const string UserId = "abc123def456abc123def456abc12345"; // 32 hex chars
    private const string OtherUserId = "ffffffffffffffffffffffffffffffff";

    // ----- GetProgress -----

    [Fact]
    public void GetProgress_ReturnsCurrentSyncSnapshot()
    {
        using var h = new ControllerTestHarness();
        SyncProgress.Start("ProgressTest", "p");
        SyncProgress.SetTotal(5);

        var result = h.Controller.GetProgress();

        Assert.IsType<OkObjectResult>(result);
        var ok = (OkObjectResult)result;
        var t = ok.Value!.GetType();
        Assert.Equal("ProgressTest", t.GetProperty("taskName")!.GetValue(ok.Value));
        Assert.Equal(5, t.GetProperty("totalItems")!.GetValue(ok.Value));
    }

    // ----- GetStats / GetHistory -----

    [Fact]
    public void GetStats_ReturnsCurrentStats()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var t = ok.Value!.GetType();
        // We don't assert exact numbers because SyncHistory persists across tests;
        // we assert only the response shape (all five stat keys present, integers).
        Assert.NotNull(t.GetProperty("total"));
        Assert.NotNull(t.GetProperty("success"));
        Assert.NotNull(t.GetProperty("failed"));
        Assert.NotNull(t.GetProperty("skipped"));
        Assert.NotNull(t.GetProperty("rewatches"));
    }

    [Fact]
    public void GetHistory_DefaultParams_ReturnsPageWithCount()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.GetHistory();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, Prop<int>(ok, "offset"));
        Assert.Equal(50, Prop<int>(ok, "count"));
    }

    [Fact]
    public void GetHistory_CapsCountAt200()
    {
        using var h = new ControllerTestHarness();

        var result = h.Controller.GetHistory(count: 9999);

        Assert.Equal(200, Prop<int>(result, "count"));
    }

    [Fact]
    public void GetHistory_NegativeOffset_ClampedToZero()
    {
        using var h = new ControllerTestHarness();

        var result = h.Controller.GetHistory(offset: -50);

        Assert.Equal(0, Prop<int>(result, "offset"));
    }

    // ----- GetAccount -----

    [Fact]
    public void GetAccount_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetAccount();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Could not determine user", Prop<string>(result, "error"));
    }

    [Fact]
    public void GetAccount_NoConfiguredAccount_ReturnsDefaultsWithIsConfiguredFalse()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.GetAccount();

        Assert.IsType<OkObjectResult>(result);
        Assert.False(Prop<bool>(result, "isConfigured"));
        Assert.False(Prop<bool>(result, "enabled"));
        Assert.True(Prop<bool>(result, "skipPreviouslySynced"));
        Assert.Equal(7, Prop<int>(result, "dateFilterDays"));
        Assert.Equal(string.Empty, Prop<string>(result, "letterboxdUsername"));
    }

    [Fact]
    public void GetAccount_WithConfiguredAccount_ReturnsAccountFields()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        var account = h.AddAccount(UserId, "8bitproxy");
        account.SyncFavorites = true;
        account.EnableDateFilter = true;
        account.DateFilterDays = 30;

        var result = h.Controller.GetAccount();

        Assert.True(Prop<bool>(result, "isConfigured"));
        Assert.Equal("8bitproxy", Prop<string>(result, "letterboxdUsername"));
        Assert.True(Prop<bool>(result, "enabled"));
        Assert.True(Prop<bool>(result, "syncFavorites"));
        Assert.True(Prop<bool>(result, "enableDateFilter"));
        Assert.Equal(30, Prop<int>(result, "dateFilterDays"));
    }

    [Fact]
    public void GetAccount_DifferentUserAccount_NotReturned()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(OtherUserId, "someoneelse");

        var result = h.Controller.GetAccount();

        Assert.False(Prop<bool>(result, "isConfigured"));
    }

    // ----- PutAccount -----

    [Fact]
    public void PutAccount_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.PutAccount(new AccountUpdateRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccount_CreatesNewAccountWhenNoneExists()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.PutAccount(new AccountUpdateRequest
        {
            LetterboxdUsername = "fresh",
            LetterboxdPassword = "pw",
            Enabled = true,
            DateFilterDays = 14,
            SyncFavorites = true
        });

        Assert.IsType<OkObjectResult>(result);
        var account = h.Config.Accounts.Single(a => a.UserJellyfinId == UserId);
        Assert.Equal("fresh", account.LetterboxdUsername);
        Assert.True(account.Enabled);
        Assert.Equal(14, account.DateFilterDays);
        Assert.True(account.SyncFavorites);
    }

    [Fact]
    public void PutAccount_UpdatesExistingAccountInPlace()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "old", enabled: false);

        h.Controller.PutAccount(new AccountUpdateRequest
        {
            LetterboxdUsername = "new",
            LetterboxdPassword = "newpw",
            Enabled = true
        });

        Assert.Single(h.Config.Accounts);
        var account = h.Config.Accounts[0];
        Assert.Equal("new", account.LetterboxdUsername);
        Assert.Equal("newpw", account.LetterboxdPassword);
        Assert.True(account.Enabled);
    }

    [Fact]
    public void PutAccount_DoesNotTouchOtherUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(OtherUserId, "untouched", enabled: true);

        h.Controller.PutAccount(new AccountUpdateRequest
        {
            LetterboxdUsername = "mine",
            Enabled = false
        });

        var other = h.Config.Accounts.Single(a => a.UserJellyfinId == OtherUserId);
        Assert.Equal("untouched", other.LetterboxdUsername);
        Assert.True(other.Enabled);
    }

    // ----- StartSync -----

    [Fact]
    public void StartSync_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartSync_NoEnabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        // No accounts at all.

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No enabled Letterboxd account", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartSync_DisabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", enabled: false);

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartSync_EnabledAccount_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync();

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.True(Prop<bool>(accepted, "started"));
    }

    // ----- StartWatchlistSync -----

    [Fact]
    public void StartWatchlistSync_NoEnabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartWatchlistSync_WatchlistDisabled_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: false);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Watchlist sync is disabled", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_WatchlistEnabled_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- PostReview validation -----

    [Fact]
    public async Task PostReview_MissingFilmSlug_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("filmSlug", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_MissingTextNotRewatch_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = null,
            IsRewatch = false
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("reviewText", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "good"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_NoEnabledAccountForUser_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", enabled: false);

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "good"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No Letterboxd account configured", Prop<string>(result, "error") ?? string.Empty);
    }

    // ----- TestConnection -----

    [Fact]
    public async Task TestConnection_MissingCredentials_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestConnection(new TestConnectionRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(Prop<bool>(result, "success"));
    }

    [Fact]
    public async Task TestConnection_OnlyUsername_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestConnection(new TestConnectionRequest
        {
            LetterboxdUsername = "user"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ----- TestJellyseerr -----

    [Fact]
    public async Task TestJellyseerr_NotConfigured_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestJellyseerr(new JellyseerrTestRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(Prop<bool>(result, "success"));
    }

    [Fact]
    public async Task TestJellyseerr_OnlyUrl_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestJellyseerr(new JellyseerrTestRequest
        {
            Url = "http://localhost:5055"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ----- GetLogs -----

    [Fact]
    public void GetLogs_LogDirectoryEmpty_ReturnsNoFiles()
    {
        using var h = new ControllerTestHarness();

        var result = h.Controller.GetLogs();

        Assert.IsType<OkObjectResult>(result);
        var lines = Prop<string[]>(result, "lines");
        Assert.NotNull(lines);
        Assert.Empty(lines!);
    }

    [Fact]
    public void GetLogs_FiltersLetterboxdSyncLines()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        System.IO.File.WriteAllText(logFile,
            "[INF] Some unrelated line\n" +
            "[INF] LetterboxdSync.Foo: relevant 1\n" +
            "[INF] Another unrelated\n" +
            "[INF] LetterboxdSync.Bar: relevant 2\n");

        var result = h.Controller.GetLogs();

        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Contains(lines, l => l.Contains("relevant 1"));
        Assert.Contains(lines, l => l.Contains("relevant 2"));
    }

    [Fact]
    public void GetLogs_RespectsMaxLinesCap()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        var lines = string.Join("\n",
            Enumerable.Range(0, 100).Select(i => $"[INF] LetterboxdSync.X: line {i}"));
        System.IO.File.WriteAllText(logFile, lines);

        var result = h.Controller.GetLogs(maxLines: 10);

        var returned = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(returned);
        Assert.Equal(10, returned!.Count);
        // The last 10 lines should be returned, ordered.
        Assert.Contains("line 99", returned[^1]);
        Assert.Equal(100, Prop<int>(result, "totalMatches"));
    }
}
