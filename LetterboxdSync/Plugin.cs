using System;
using System.Collections.Generic;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace LetterboxdSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "LetterboxdSync";

    public override Guid Id => Guid.Parse("c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "letterboxdsync",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "Letterboxd Sync",
            },
            new PluginPageInfo
            {
                Name = "letterboxdsyncjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.js"
            }
        };
    }
}
