using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace LetterboxdSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Account> Accounts { get; set; } = new List<Account>();

    /// <summary>
    /// Base URL of the Jellyseerr instance, e.g. "http://192.168.1.122:5055" or "https://requests.example.com".
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Jellyseerr API key (Settings → General → API Key in Jellyseerr).
    /// </summary>
    public string? JellyseerrApiKey { get; set; }
}
