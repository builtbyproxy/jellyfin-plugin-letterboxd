using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
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

    // ----- GetAccounts / PutAccounts (multi-account, per-user) -----

    [Fact]
    public void GetAccounts_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetAccounts();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetAccounts_ReturnsOnlyCallingUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine-a");
        h.AddAccount(UserId, "mine-b");
        h.AddAccount(OtherUserId, "someone-else");

        var result = h.Controller.GetAccounts();

        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = Prop<System.Collections.IEnumerable>(ok, "accounts")!;
        var list = accounts.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
        var usernames = list.Select(a => a.GetType().GetProperty("letterboxdUsername")!.GetValue(a)?.ToString()).ToHashSet();
        Assert.Contains("mine-a", usernames);
        Assert.Contains("mine-b", usernames);
        Assert.DoesNotContain("someone-else", usernames);
    }

    [Fact]
    public void GetAccounts_OrdersPrimaryFirst()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "secondary");
        var primary = h.AddAccount(UserId, "primary");
        primary.IsPrimary = true;

        var result = h.Controller.GetAccounts();

        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = Prop<System.Collections.IEnumerable>(ok, "accounts")!;
        var first = accounts.Cast<object>().First();
        Assert.Equal("primary", first.GetType().GetProperty("letterboxdUsername")!.GetValue(first));
    }

    [Fact]
    public void PutAccounts_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_NullAccountsList_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest { Accounts = null! });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_EmptyUsername_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "ok" },
                new AccountUpdateRequest { LetterboxdUsername = "   " }
            }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("#2", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void PutAccounts_ReplacesCallingUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "old-a");
        h.AddAccount(UserId, "old-b");

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "new-1", Enabled = true, IsPrimary = true },
                new AccountUpdateRequest { LetterboxdUsername = "new-2", Enabled = true }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Equal(2, mine.Count);
        var names = mine.Select(a => a.LetterboxdUsername).ToHashSet();
        Assert.Equal(new[] { "new-1", "new-2" }.ToHashSet(), names);
    }

    [Fact]
    public void PutAccounts_PreservesOtherUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(OtherUserId, "untouched", enabled: true);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "mine", Enabled = true } }
        });

        var other = h.Config.Accounts.Single(a => a.UserJellyfinId == OtherUserId);
        Assert.Equal("untouched", other.LetterboxdUsername);
        Assert.True(other.Enabled);
    }

    [Fact]
    public void PutAccounts_StampsCallingUserIdOntoEveryRow()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        // The request shape doesn't even carry UserJellyfinId, but a hostile or
        // confused client might try to spoof one via reflection or a custom payload.
        // The endpoint must overwrite to the calling user's id regardless.
        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "mine", Enabled = true } }
        });

        var mine = h.Config.Accounts.Single(a => a.LetterboxdUsername == "mine");
        Assert.Equal(UserId, mine.UserJellyfinId);
    }

    [Fact]
    public void PutAccounts_NormalisesPrimaryFlag_AutoPromotesFirstWhenNoneMarked()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "a", Enabled = true },
                new AccountUpdateRequest { LetterboxdUsername = "b", Enabled = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Single(mine.Where(a => a.IsPrimary));
    }

    [Fact]
    public void PutAccounts_NormalisesPrimaryFlag_DemotesExtras()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "a", Enabled = true, IsPrimary = true },
                new AccountUpdateRequest { LetterboxdUsername = "b", Enabled = true, IsPrimary = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Single(mine.Where(a => a.IsPrimary));
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
        // Without a specific letterboxdUsername, the multi-account controller returns
        // the broader "no eligible accounts" error rather than the per-account
        // "Watchlist sync is disabled" message.
        Assert.Contains("No enabled accounts with watchlist sync",
            Prop<string>(result, "error") ?? string.Empty);
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
        // Multi-account controller pluralises ("No Letterboxd accounts configured"),
        // singular form remained on main pre-multi-account; assert on the shared prefix.
        Assert.Contains("No Letterboxd account", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_SuccessfulFlow_ReturnsOkAndCallsService()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                Rating = 4.5,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.True(Prop<bool>(result, "success"));
            await service.Received(1).PostReviewAsync(
                "sinners", "great", false, false, null, 4.5, 1233413);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_ServiceThrows_ReturnsBadRequestWithError()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        service.When(s => s.PostReviewAsync(
            NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<string?>(),
            NSubstitute.Arg.Any<bool>(), NSubstitute.Arg.Any<bool>(),
            NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<double?>(),
            NSubstitute.Arg.Any<int?>())).Do(_ => throw new System.Exception("Cloudflare 403"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                TmdbId = 1233413
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Cloudflare", Prop<string>(result, "error") ?? string.Empty);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_RewatchWithoutText_AllowedThroughValidation()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = null,
                IsRewatch = true,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            await service.Received(1).PostReviewAsync(
                "sinners", null, false, true,
                NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<double?>(), 1233413);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
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
            "[2026-05-05 10:00:00.000 +00:00] [INF] [1] Some.Other: unrelated line\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] LetterboxdSync.Foo: relevant 1\n" +
            "[2026-05-05 10:00:02.000 +00:00] [INF] [1] Another.Thing: unrelated\n" +
            "[2026-05-05 10:00:03.000 +00:00] [INF] [1] LetterboxdSync.Bar: relevant 2\n");

        var result = h.Controller.GetLogs();

        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Contains(lines, l => l.Contains("relevant 1"));
        Assert.Contains(lines, l => l.Contains("relevant 2"));
    }

    [Fact]
    public void GetLogs_CapturesMultiLineExceptionContinuations()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        // A real multi-line error: the header carries the tag; the exception message
        // and stack frames continue on lines that do NOT carry it.
        System.IO.File.WriteAllText(logFile,
            "[2026-05-05 10:00:00.000 +00:00] [ERR] [1] LetterboxdSync.PlaybackHandler: sync failed\n" +
            "System.Exception: something broke\n" +
            "   at LetterboxdSync.PlaybackHandler.Do()\n" +
            "   at System.Threading.Tasks.Task.Run()\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] Other.Thing: unrelated, must NOT be captured\n");

        var result = h.Controller.GetLogs();
        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");

        Assert.NotNull(lines);
        // Header + 3 continuation lines, and nothing from the unrelated entry after it.
        Assert.Equal(4, lines!.Count);
        Assert.Contains(lines, l => l.Contains("System.Exception: something broke"));
        Assert.Contains(lines, l => l.Contains("at LetterboxdSync.PlaybackHandler.Do()"));
        Assert.DoesNotContain(lines, l => l.Contains("must NOT be captured"));
    }

    [Fact]
    public void GetLogs_NegativeMaxLines_DoesNotThrow()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        System.IO.File.WriteAllText(logFile,
            "[2026-05-05 10:00:00.000 +00:00] [INF] [1] LetterboxdSync.Foo: a\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] LetterboxdSync.Foo: b\n");

        // Negative maxLines must clamp, not crash with ArgumentOutOfRangeException.
        var result = h.Controller.GetLogs(maxLines: -1);
        Assert.IsType<OkObjectResult>(result);
        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(lines);
        Assert.NotEmpty(lines!);
    }

    [Fact]
    public void GetLogs_RespectsMaxLinesCap()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        var lines = string.Join("\n",
            Enumerable.Range(0, 100).Select(i => $"[2026-05-05 10:00:{i % 60:D2}.000 +00:00] [INF] [1] LetterboxdSync.X: line {i}"));
        System.IO.File.WriteAllText(logFile, lines);

        var result = h.Controller.GetLogs(maxLines: 10);

        var returned = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(returned);
        Assert.Equal(10, returned!.Count);
        // The last 10 lines should be returned, ordered.
        Assert.Contains("line 99", returned[^1]);
        Assert.Equal(100, Prop<int>(result, "totalMatches"));
    }

    [Fact]
    public void GetLogs_LogDirectoryMissing_ReturnsErrorPayload()
    {
        using var h = new ControllerTestHarness();
        // Point the log directory at a path that doesn't exist → directory-not-found branch.
        h.AppPaths.LogDirectoryPath.Returns(
            System.IO.Path.Combine(h.TempDir, "does-not-exist-" + Guid.NewGuid().ToString("N")));

        var result = h.Controller.GetLogs();

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Prop<string[]>(result, "lines")!);
        Assert.Contains("log directory not found", Prop<string>(result, "error") ?? string.Empty);
    }

    // ----- StartSync: conflict + named-account targeting -----

    [Fact]
    public void StartSync_SyncAlreadyRunning_ReturnsConflict()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        // Hold the global gate so LetterboxdSyncRunner.IsRunning reports true.
        Assert.True(SyncGate.Instance.Wait(0));
        try
        {
            var result = h.Controller.StartSync();
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    [Fact]
    public void StartSync_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync(letterboxdUsername: "someone-else");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("someone-else", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartSync_NamedAccountFound_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- StartWatchlistSync: user, conflict, named-account -----

    [Fact]
    public void StartWatchlistSync_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartWatchlistSync_SyncAlreadyRunning_ReturnsConflict()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        Assert.True(SyncGate.Instance.Wait(0));
        try
        {
            var result = h.Controller.StartWatchlistSync();
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "ghost");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ghost", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountWatchlistDisabled_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: false);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("disabled", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountEnabled_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- TestConnection: success + failure via the factory seam -----

    [Fact]
    public async Task TestConnection_AuthSucceeds_ReturnsOk()
    {
        using var h = new ControllerTestHarness();

        var service = Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.TestConnection(new TestConnectionRequest
            {
                LetterboxdUsername = "8bitproxy",
                LetterboxdPassword = "secret"
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.True(Prop<bool>(result, "success"));
            Assert.Equal("8bitproxy", Prop<string>(result, "letterboxdUsername"));
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task TestConnection_AuthThrows_ReturnsBadRequestWithError()
    {
        using var h = new ControllerTestHarness();

        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            throw new Exception("bad credentials");
        try
        {
            var result = await h.Controller.TestConnection(new TestConnectionRequest
            {
                LetterboxdUsername = "8bitproxy",
                LetterboxdPassword = "wrong"
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(Prop<bool>(result, "success"));
            Assert.Contains("bad credentials", Prop<string>(result, "error") ?? string.Empty);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    // ----- PostReview: named-account targeting + Jellyfin rating writeback -----

    [Fact]
    public async Task PostReview_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "great",
            LetterboxdUsername = "not-mine"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not-mine", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_NamedAccountFound_PostsToThatAccountOnly()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");
        h.AddAccount(UserId, "deb");

        var service = Substitute.For<ILetterboxdService>();
        // Capture which Letterboxd username the factory was asked to authenticate.
        var authedUsernames = new System.Collections.Generic.List<string>();
        LetterboxdServiceFactory.OverrideForTesting = (u, _, _, _, _) =>
        {
            authedUsernames.Add(u);
            return System.Threading.Tasks.Task.FromResult(service);
        };
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                LetterboxdUsername = "deb"
            });

            Assert.IsType<OkObjectResult>(result);
            // Only the named account was targeted, not the fan-out across both.
            Assert.Equal(new[] { "deb" }, authedUsernames);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_SuccessWithRating_MirrorsRatingIntoJellyfin()
    {
        // Real User/Movie instances: the controller matches the claim id against
        // User.Id.ToString("N"), and User has no parameterless ctor to substitute.
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");

        using var h = new ControllerTestHarness(currentUserId: userId);
        h.AddAccount(userId, "8bitproxy");
        h.UserManager.GetUsers().Returns(new[] { user });

        var movie = new Movie { Name = "Sinners" };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");
        h.LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie });

        var userData = new UserItemData { Key = "k" };
        h.UserDataManager.GetUserData(user, movie).Returns(userData);

        var service = Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                Rating = 4.5,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            // 4.5 Letterboxd stars → 9.0 Jellyfin rating, written back and persisted.
            Assert.Equal(9.0, userData.Rating);
            h.UserDataManager.Received(1).SaveUserData(
                user, movie, userData,
                MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating,
                Arg.Any<System.Threading.CancellationToken>());
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }
}
