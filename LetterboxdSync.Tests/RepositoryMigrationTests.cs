using LetterboxdSync;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Updates;
using Xunit;

namespace LetterboxdSync.Tests;

public class RepositoryMigrationTests
{
    private static ServerConfiguration ConfigWith(params RepositoryInfo[] repositories)
        => new() { PluginRepositories = repositories };

    [Fact]
    public void Rewrites_RawGitHubUrl_To_ProxiedUrl()
    {
        var config = ConfigWith(new RepositoryInfo
        {
            Name = "Letterboxd Sync",
            Url = RepositoryMigrator.RawGitHubManifestUrl,
            Enabled = true,
        });

        var changed = RepositoryMigrator.TryMigrate(config);

        Assert.True(changed);
        var entry = Assert.Single(config.PluginRepositories);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, entry.Url);
        Assert.Equal("Letterboxd Sync", entry.Name);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void Preserves_Disabled_State_When_Rewriting()
    {
        var config = ConfigWith(new RepositoryInfo
        {
            Name = "Letterboxd Sync",
            Url = RepositoryMigrator.RawGitHubManifestUrl,
            Enabled = false,
        });

        Assert.True(RepositoryMigrator.TryMigrate(config));
        var entry = Assert.Single(config.PluginRepositories);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, entry.Url);
        Assert.False(entry.Enabled);
    }

    [Theory]
    [InlineData("HTTPS://RAW.GITHUBUSERCONTENT.COM/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json")]
    [InlineData("https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json/")]
    [InlineData("  https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json ")]
    public void Matches_Old_Url_Ignoring_Case_TrailingSlash_And_Whitespace(string url)
    {
        var config = ConfigWith(new RepositoryInfo { Name = "Letterboxd Sync", Url = url });

        Assert.True(RepositoryMigrator.TryMigrate(config));
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, Assert.Single(config.PluginRepositories).Url);
    }

    [Fact]
    public void Removes_Old_Entry_When_Proxied_Entry_Already_Exists()
    {
        var config = ConfigWith(
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl },
            new RepositoryInfo { Name = "Letterboxd Sync (proxied)", Url = RepositoryMigrator.ProxiedManifestUrl });

        Assert.True(RepositoryMigrator.TryMigrate(config));
        var entry = Assert.Single(config.PluginRepositories);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, entry.Url);
        Assert.Equal("Letterboxd Sync (proxied)", entry.Name);
    }

    [Fact]
    public void NoOp_When_Already_On_Proxied_Url()
    {
        var config = ConfigWith(new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.ProxiedManifestUrl });

        Assert.False(RepositoryMigrator.TryMigrate(config));
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, Assert.Single(config.PluginRepositories).Url);
    }

    [Fact]
    public void Leaves_Unrelated_Repositories_Alone()
    {
        var thirdParty = new RepositoryInfo
        {
            Name = "File Transformation",
            Url = "https://www.iamparadox.dev/jellyfin/plugins/manifest.json",
        };
        var config = ConfigWith(
            thirdParty,
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl });

        Assert.True(RepositoryMigrator.TryMigrate(config));
        Assert.Equal(2, config.PluginRepositories.Length);
        Assert.Equal("https://www.iamparadox.dev/jellyfin/plugins/manifest.json", config.PluginRepositories[0].Url);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, config.PluginRepositories[1].Url);
    }

    [Fact]
    public void NoOp_When_No_Repositories_Configured()
    {
        Assert.False(RepositoryMigrator.TryMigrate(ConfigWith()));
    }

    [Fact]
    public void NoOp_When_Plugin_Not_In_Catalog()
    {
        var config = ConfigWith(new RepositoryInfo { Name = "Other", Url = "https://example.com/manifest.json" });

        Assert.False(RepositoryMigrator.TryMigrate(config));
    }
}
