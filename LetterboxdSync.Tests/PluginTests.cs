using System;
using System.IO;
using System.Linq;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class PluginTests : IDisposable
{
    private readonly string _tempDir;

    public PluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private Plugin MakePlugin()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());

        return new Plugin(paths, xml);
    }

    [Fact]
    public void Plugin_NameIsLetterboxdSync()
    {
        var plugin = MakePlugin();
        Assert.Equal("LetterboxdSync", plugin.Name);
    }

    [Fact]
    public void Plugin_HasStableGuid()
    {
        var plugin = MakePlugin();
        // The plugin's GUID is part of its installable manifest entry; changing it
        // would break upgrades for existing users. Pin the value here.
        Assert.Equal(Guid.Parse("c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35"), plugin.Id);
    }

    [Fact]
    public void Plugin_ConstructorSetsInstance()
    {
        var plugin = MakePlugin();
        Assert.Same(plugin, Plugin.Instance);
    }

    [Fact]
    public void GetPages_ContainsConfigPage()
    {
        var plugin = MakePlugin();
        var pages = plugin.GetPages().ToList();

        var config = pages.FirstOrDefault(p => p.Name == "letterboxdsync");
        Assert.NotNull(config);
        Assert.True(config!.EnableInMainMenu);
        Assert.Equal("Letterboxd Sync", config.DisplayName);
        Assert.Contains("configPage.html", config.EmbeddedResourcePath);
    }

    [Fact]
    public void GetPages_ContainsAllExpectedPages()
    {
        // The plugin registers four embedded resources: the main config page, its
        // companion JS, the stats page, and the sidebar-injected user page. The
        // sidebar.js script is served separately as a resource via configPage.html.
        var plugin = MakePlugin();
        var names = plugin.GetPages().Select(p => p.Name).ToHashSet();

        Assert.Contains("letterboxdsync", names);
        Assert.Contains("letterboxdsyncjs", names);
        Assert.Contains("letterboxdstats", names);
        Assert.Contains("letterboxduser", names);
    }

    [Fact]
    public void GetPages_AllPointAtEmbeddedResources()
    {
        var plugin = MakePlugin();

        foreach (var page in plugin.GetPages())
        {
            Assert.False(string.IsNullOrEmpty(page.EmbeddedResourcePath));
            Assert.StartsWith("LetterboxdSync.Web.", page.EmbeddedResourcePath);
        }
    }
}
