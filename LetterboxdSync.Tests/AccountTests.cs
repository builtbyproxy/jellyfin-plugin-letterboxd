using System.Collections.Generic;
using System.Linq;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using Xunit;

namespace LetterboxdSync.Tests;

public class AccountUpdateRequestTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var req = new AccountUpdateRequest();

        Assert.Equal(string.Empty, req.LetterboxdUsername);
        Assert.Equal(string.Empty, req.LetterboxdPassword);
        Assert.Null(req.RawCookies);
        Assert.False(req.Enabled);
        Assert.False(req.SyncFavorites);
        Assert.False(req.EnableDateFilter);
        Assert.Equal(7, req.DateFilterDays);
        Assert.False(req.EnableWatchlistSync);
        Assert.False(req.EnableDiaryImport);
    }

    [Fact]
    public void HasNoUserJellyfinIdField()
    {
        // Security: AccountUpdateRequest must NOT have UserJellyfinId
        // to prevent users from writing to another user's account
        var properties = typeof(AccountUpdateRequest).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name == "UserJellyfinId");
    }

    [Fact]
    public void AllAccountFieldsAreMapped()
    {
        // Every writable field on Account (except UserJellyfinId) should have
        // a corresponding field on AccountUpdateRequest
        var accountProps = typeof(Account).GetProperties()
            .Where(p => p.Name != "UserJellyfinId")
            .Select(p => p.Name)
            .ToHashSet();

        var requestProps = typeof(AccountUpdateRequest).GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        foreach (var prop in accountProps)
        {
            Assert.Contains(prop, requestProps);
        }
    }
}

public class TestConnectionRequestTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var req = new TestConnectionRequest();

        Assert.Equal(string.Empty, req.LetterboxdUsername);
        Assert.Equal(string.Empty, req.LetterboxdPassword);
        Assert.Null(req.RawCookies);
    }

    [Fact]
    public void HasNoUserJellyfinIdField()
    {
        var properties = typeof(TestConnectionRequest).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name == "UserJellyfinId");
    }
}

public class PluginConfigurationAccountTests
{
    [Fact]
    public void FindOrCreate_FindsExistingAccount()
    {
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account
        {
            UserJellyfinId = "abc123",
            LetterboxdUsername = "existing"
        });

        var account = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "abc123");

        Assert.NotNull(account);
        Assert.Equal("existing", account!.LetterboxdUsername);
    }

    [Fact]
    public void FindOrCreate_CreatesNewWhenNotFound()
    {
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account
        {
            UserJellyfinId = "abc123",
            LetterboxdUsername = "existing"
        });

        var account = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "xyz789");
        Assert.Null(account);

        // Simulate the PutAccount find-or-create pattern
        account = new Account { UserJellyfinId = "xyz789" };
        config.Accounts.Add(account);

        Assert.Equal(2, config.Accounts.Count);
        Assert.Equal("xyz789", config.Accounts[1].UserJellyfinId);
    }

    [Fact]
    public void FindOrCreate_UpdatesExistingInPlace()
    {
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account
        {
            UserJellyfinId = "abc123",
            LetterboxdUsername = "old_username",
            Enabled = false
        });

        var account = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "abc123")!;
        account.LetterboxdUsername = "new_username";
        account.Enabled = true;

        // Should modify in place, not create duplicate
        Assert.Single(config.Accounts);
        Assert.Equal("new_username", config.Accounts[0].LetterboxdUsername);
        Assert.True(config.Accounts[0].Enabled);
    }

    [Fact]
    public void AccountLookup_MatchesOnUserJellyfinIdOnly()
    {
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account { UserJellyfinId = "user1", LetterboxdUsername = "lb_user1" });
        config.Accounts.Add(new Account { UserJellyfinId = "user2", LetterboxdUsername = "lb_user2" });

        var found = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "user2");

        Assert.NotNull(found);
        Assert.Equal("lb_user2", found!.LetterboxdUsername);
    }

    [Fact]
    public void ApplyUpdateRequest_CopiesAllFields()
    {
        var account = new Account { UserJellyfinId = "user1" };
        var request = new AccountUpdateRequest
        {
            LetterboxdUsername = "testuser",
            LetterboxdPassword = "testpass",
            RawCookies = "cf_clearance=abc",
            Enabled = true,
            SyncFavorites = true,
            EnableDateFilter = true,
            DateFilterDays = 14,
            EnableWatchlistSync = true,
            EnableDiaryImport = true
        };

        // Simulate the PutAccount field copy
        account.LetterboxdUsername = request.LetterboxdUsername;
        account.LetterboxdPassword = request.LetterboxdPassword;
        account.RawCookies = request.RawCookies;
        account.Enabled = request.Enabled;
        account.SyncFavorites = request.SyncFavorites;
        account.EnableDateFilter = request.EnableDateFilter;
        account.DateFilterDays = request.DateFilterDays;
        account.EnableWatchlistSync = request.EnableWatchlistSync;
        account.EnableDiaryImport = request.EnableDiaryImport;

        Assert.Equal("testuser", account.LetterboxdUsername);
        Assert.Equal("testpass", account.LetterboxdPassword);
        Assert.Equal("cf_clearance=abc", account.RawCookies);
        Assert.True(account.Enabled);
        Assert.True(account.SyncFavorites);
        Assert.True(account.EnableDateFilter);
        Assert.Equal(14, account.DateFilterDays);
        Assert.True(account.EnableWatchlistSync);
        Assert.True(account.EnableDiaryImport);
        // UserJellyfinId must not change
        Assert.Equal("user1", account.UserJellyfinId);
    }

    [Fact]
    public void MultipleUsers_IndependentAccounts()
    {
        var config = new PluginConfiguration();

        // Simulate two users saving their accounts
        var user1 = new Account { UserJellyfinId = "aaa", LetterboxdUsername = "alice", Enabled = true };
        var user2 = new Account { UserJellyfinId = "bbb", LetterboxdUsername = "bob", Enabled = true };
        config.Accounts.Add(user1);
        config.Accounts.Add(user2);

        // User1 updates their password
        var found = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "aaa")!;
        found.LetterboxdPassword = "newpass";

        // User2 should be unaffected
        var other = config.Accounts.FirstOrDefault(a => a.UserJellyfinId == "bbb")!;
        Assert.Equal(string.Empty, other.LetterboxdPassword);
        Assert.Equal("bob", other.LetterboxdUsername);
    }
}
