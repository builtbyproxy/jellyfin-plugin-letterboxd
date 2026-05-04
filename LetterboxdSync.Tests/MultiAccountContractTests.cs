using System.Linq;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Wiring contracts for the multi-account feature: data fields propagate end-to-end
/// across the Account model, the API request DTOs, and the review fan-out request.
/// These are intentionally simple, since the runtime fan-out behaviour relies on
/// Jellyfin services that are awkward to mock at unit-test scope.
/// </summary>
public class MultiAccountContractTests
{
    [Fact]
    public void Account_IsPrimary_DefaultsFalse()
    {
        var account = new Account();
        Assert.False(account.IsPrimary);
    }

    [Fact]
    public void Account_PlaylistName_DefaultsNull()
    {
        var account = new Account();
        Assert.Null(account.PlaylistName);
    }

    [Fact]
    public void AccountUpdateRequest_HasIsPrimaryField()
    {
        var props = typeof(AccountUpdateRequest).GetProperties();
        Assert.Contains(props, p => p.Name == "IsPrimary" && p.PropertyType == typeof(bool));
    }

    [Fact]
    public void AccountUpdateRequest_HasPlaylistNameField()
    {
        var props = typeof(AccountUpdateRequest).GetProperties();
        Assert.Contains(props, p => p.Name == "PlaylistName" && p.PropertyType == typeof(string));
    }

    [Fact]
    public void ReviewRequest_HasLetterboxdUsernameField()
    {
        // The dashboard's "Post as" dropdown sends LetterboxdUsername; the controller
        // uses it to target a single account. Without this field the dropdown would
        // silently always fan out, breaking the per-account selection promise.
        var props = typeof(ReviewRequest).GetProperties();
        Assert.Contains(props, p => p.Name == "LetterboxdUsername" && p.PropertyType == typeof(string));
    }

    [Fact]
    public void GetEnabledAccountsForUser_PrimaryWinsConflict()
    {
        // Models the diary-import merge contract: walking the helper's output in order
        // and taking the first non-null rating for each TMDb ID gives "primary wins".
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account
        {
            UserJellyfinId = "u1", LetterboxdUsername = "secondary", Enabled = true
        });
        config.Accounts.Add(new Account
        {
            UserJellyfinId = "u1", LetterboxdUsername = "primary", Enabled = true, IsPrimary = true
        });

        var ordered = config.GetEnabledAccountsForUser("u1").ToList();

        // Primary always emitted first, regardless of insertion order.
        Assert.Equal("primary", ordered[0].LetterboxdUsername);
        Assert.Equal("secondary", ordered[1].LetterboxdUsername);
    }

    [Fact]
    public void NormalisePrimaryFlags_RunsOnPutAccountSemantics()
    {
        // Sanity-check: the controller's PutAccount calls NormalisePrimaryFlags after
        // applying request fields. Two enabled accounts with no primary marked should
        // collapse to exactly one primary on the next save cycle.
        var config = new PluginConfiguration();
        config.Accounts.Add(new Account { UserJellyfinId = "u1", LetterboxdUsername = "a", Enabled = true });
        config.Accounts.Add(new Account { UserJellyfinId = "u1", LetterboxdUsername = "b", Enabled = true });

        config.NormalisePrimaryFlags();

        Assert.Single(config.Accounts.Where(a => a.IsPrimary));
    }
}
