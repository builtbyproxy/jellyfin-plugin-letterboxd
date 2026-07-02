using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace LetterboxdSync;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Capture the Jellyfin version at startup so it's available immediately — not
        // only after the daily telemetry task first runs. A diagnostic bundle sent in
        // the first minutes after install (exactly when first-run problems surface)
        // must still know the Jellyfin version.
        var version = applicationHost?.ApplicationVersionString;
        if (!string.IsNullOrEmpty(version))
            TelemetryService.JellyfinVersion = version;

        serviceCollection.AddSingleton<LetterboxdSyncRunner>();
        serviceCollection.AddSingleton<WatchlistSyncRunner>();
        serviceCollection.AddHostedService<PlaybackHandler>();
        serviceCollection.AddHostedService<RepositoryMigrationService>();
    }
}
