using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// One-shot startup migration that rewrites this plugin's catalog repository entry
/// from the raw GitHub manifest URL to the Cloudflare Worker's proxied manifest URL.
/// The Worker serves the identical manifest (edge-cached) and counting its polls is
/// the only way to measure the active install base continuously — downloads already
/// route through the Worker, but those are only observable at each release's update
/// wave. Only our own repository entry is ever touched; any other catalog entry the
/// user has configured is left alone.
/// </summary>
public class RepositoryMigrationService : IHostedService
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<RepositoryMigrationService> _logger;

    public RepositoryMigrationService(
        IServerConfigurationManager configurationManager,
        ILogger<RepositoryMigrationService> logger)
    {
        _configurationManager = configurationManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Best-effort: a failure here must never affect plugin (or server) startup.
        try
        {
            if (RepositoryMigrator.TryMigrate(_configurationManager.Configuration))
            {
                _configurationManager.SaveConfiguration();
                _logger.LogInformation(
                    "Migrated LetterboxdSync plugin repository to {Url}",
                    RepositoryMigrator.ProxiedManifestUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LetterboxdSync repository migration failed; leaving catalog entry unchanged");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Pure migration logic, separated from the hosted service so tests can exercise it
/// against a plain <see cref="ServerConfiguration"/> without a running server.
/// </summary>
internal static class RepositoryMigrator
{
    internal const string RawGitHubManifestUrl =
        "https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json";

    internal const string ProxiedManifestUrl =
        "https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json";

    /// <summary>
    /// Rewrites the raw-GitHub repository entry to the proxied URL, or removes it if the
    /// proxied entry already exists (avoids a duplicate catalog source). Returns whether
    /// the configuration was modified and needs saving.
    /// </summary>
    internal static bool TryMigrate(ServerConfiguration configuration)
    {
        var repositories = configuration.PluginRepositories;
        if (repositories is null || repositories.Length == 0)
            return false;

        var oldEntries = repositories.Where(r => UrlEquals(r.Url, RawGitHubManifestUrl)).ToArray();
        if (oldEntries.Length == 0)
            return false;

        if (repositories.Any(r => UrlEquals(r.Url, ProxiedManifestUrl)))
        {
            configuration.PluginRepositories = repositories.Except(oldEntries).ToArray();
            return true;
        }

        // Rewrite in place, keeping the user's chosen display name and enabled state.
        foreach (var entry in oldEntries)
            entry.Url = ProxiedManifestUrl;

        return true;
    }

    private static bool UrlEquals(string? url, string expected)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return string.Equals(url.Trim().TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);
    }
}
