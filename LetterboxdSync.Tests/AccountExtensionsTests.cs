using System.Linq;
using LetterboxdSync.Configuration;
using Xunit;

namespace LetterboxdSync.Tests;

public class AccountExtensionsTests
{
    private static PluginConfiguration MakeConfig(params Account[] accounts)
    {
        var c = new PluginConfiguration();
        c.Accounts.AddRange(accounts);
        return c;
    }

    private static Account MakeAccount(string userId, string lbUser, bool enabled = true, bool primary = false)
        => new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = lbUser,
            Enabled = enabled,
            IsPrimary = primary
        };

    [Fact]
    public void GetEnabledAccountsForUser_ReturnsOnlyEnabledMatching()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob", enabled: false),
            MakeAccount("u2", "carol"),
            MakeAccount("u1", "dan"));

        var result = config.GetEnabledAccountsForUser("u1").ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.LetterboxdUsername == "alice");
        Assert.Contains(result, a => a.LetterboxdUsername == "dan");
    }

    [Fact]
    public void GetEnabledAccountsForUser_PrimaryFirst()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob"),
            MakeAccount("u1", "carol", primary: true));

        var ordered = config.GetEnabledAccountsForUser("u1").ToList();

        Assert.Equal("carol", ordered[0].LetterboxdUsername);
    }

    [Fact]
    public void GetEnabledAccountsForUser_UnknownUser_ReturnsEmpty()
    {
        var config = MakeConfig(MakeAccount("u1", "alice"));
        Assert.Empty(config.GetEnabledAccountsForUser("u-other"));
    }

    [Fact]
    public void GetEnabledAccountsForUser_EmptyUserId_ReturnsEmpty()
    {
        var config = MakeConfig(MakeAccount("u1", "alice"));
        Assert.Empty(config.GetEnabledAccountsForUser(""));
    }

    [Fact]
    public void GetPrimaryAccountForUser_ExplicitPrimary_Returned()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob", primary: true));

        var primary = config.GetPrimaryAccountForUser("u1");

        Assert.NotNull(primary);
        Assert.Equal("bob", primary!.LetterboxdUsername);
    }

    [Fact]
    public void GetPrimaryAccountForUser_NoExplicitPrimary_FallsBackToFirstEnabled()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice", enabled: false),
            MakeAccount("u1", "bob"),
            MakeAccount("u1", "carol"));

        var primary = config.GetPrimaryAccountForUser("u1");

        Assert.NotNull(primary);
        Assert.Equal("bob", primary!.LetterboxdUsername);
    }

    [Fact]
    public void GetPrimaryAccountForUser_NoAccounts_ReturnsNull()
    {
        var config = MakeConfig();
        Assert.Null(config.GetPrimaryAccountForUser("u1"));
    }

    [Fact]
    public void FindAccount_MatchingUsername_Returned()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob"));

        var found = config.FindAccount("u1", "bob");

        Assert.NotNull(found);
        Assert.Equal("bob", found!.LetterboxdUsername);
    }

    [Fact]
    public void FindAccount_CaseInsensitive()
    {
        var config = MakeConfig(MakeAccount("u1", "Alice"));
        Assert.NotNull(config.FindAccount("u1", "alice"));
        Assert.NotNull(config.FindAccount("u1", "ALICE"));
    }

    [Fact]
    public void FindAccount_DisabledAccount_NotReturned()
    {
        var config = MakeConfig(MakeAccount("u1", "alice", enabled: false));
        Assert.Null(config.FindAccount("u1", "alice"));
    }

    [Fact]
    public void FindAccount_WrongUser_NotReturned()
    {
        var config = MakeConfig(MakeAccount("u1", "alice"));
        Assert.Null(config.FindAccount("u2", "alice"));
    }

    [Fact]
    public void NormalisePrimaryFlags_NoPrimaryMarked_AutoPromotesFirst()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob"));

        var changed = config.NormalisePrimaryFlags();

        Assert.True(changed);
        Assert.True(config.Accounts[0].IsPrimary);
        Assert.False(config.Accounts[1].IsPrimary);
    }

    [Fact]
    public void NormalisePrimaryFlags_OneAlreadyPrimary_NoChange()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u1", "bob", primary: true));

        var changed = config.NormalisePrimaryFlags();

        Assert.False(changed);
        Assert.False(config.Accounts[0].IsPrimary);
        Assert.True(config.Accounts[1].IsPrimary);
    }

    [Fact]
    public void NormalisePrimaryFlags_MultiplePrimaries_DemotesExtras()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice", primary: true),
            MakeAccount("u1", "bob", primary: true),
            MakeAccount("u1", "carol", primary: true));

        var changed = config.NormalisePrimaryFlags();

        Assert.True(changed);
        Assert.True(config.Accounts[0].IsPrimary);
        Assert.False(config.Accounts[1].IsPrimary);
        Assert.False(config.Accounts[2].IsPrimary);
    }

    [Fact]
    public void NormalisePrimaryFlags_PerUserScoping_DoesNotCrossUsers()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice", primary: true),
            MakeAccount("u2", "bob"),
            MakeAccount("u2", "carol"));

        config.NormalisePrimaryFlags();

        Assert.True(config.Accounts[0].IsPrimary);
        // u2 had no primary, so u2's first account should now be primary.
        Assert.True(config.Accounts[1].IsPrimary);
        Assert.False(config.Accounts[2].IsPrimary);
    }

    [Fact]
    public void NormalisePrimaryFlags_DisabledAccountsIgnored()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice", enabled: false, primary: true),
            MakeAccount("u1", "bob"));

        config.NormalisePrimaryFlags();

        // Only the enabled account is considered for the per-user primary check,
        // so bob (the only enabled one) becomes primary.
        Assert.True(config.Accounts[1].IsPrimary);
    }

    [Fact]
    public void NormalisePrimaryFlags_Idempotent()
    {
        var config = MakeConfig(
            MakeAccount("u1", "alice"),
            MakeAccount("u2", "bob"));

        var firstRun = config.NormalisePrimaryFlags();
        var secondRun = config.NormalisePrimaryFlags();

        Assert.True(firstRun);
        Assert.False(secondRun);
    }

    [Fact]
    public void GetPlaylistName_DefaultPattern()
    {
        var account = MakeAccount("u1", "8bitproxy");
        Assert.Equal("Letterboxd Watchlist (8bitproxy)", account.GetPlaylistName());
    }

    [Fact]
    public void GetPlaylistName_OverrideUsed()
    {
        var account = MakeAccount("u1", "8bitproxy");
        account.PlaylistName = "My Movies";
        Assert.Equal("My Movies", account.GetPlaylistName());
    }

    [Fact]
    public void GetPlaylistName_OverrideTrimmed()
    {
        var account = MakeAccount("u1", "8bitproxy");
        account.PlaylistName = "  Padded  ";
        Assert.Equal("Padded", account.GetPlaylistName());
    }

    [Fact]
    public void GetPlaylistName_BlankOverrideFallsBackToDefault()
    {
        var account = MakeAccount("u1", "8bitproxy");
        account.PlaylistName = "   ";
        Assert.Equal("Letterboxd Watchlist (8bitproxy)", account.GetPlaylistName());
    }
}
