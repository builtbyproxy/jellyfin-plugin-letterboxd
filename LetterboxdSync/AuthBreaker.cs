using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// One account's breaker state. Closed means logins proceed normally;
/// open means every sync entry point skips the account without a login attempt.
/// </summary>
public class AuthBreakerEntry
{
    public string UserJellyfinId { get; set; } = string.Empty;

    public string LetterboxdUsername { get; set; } = string.Empty;

    public int ConsecutiveFailures { get; set; }

    /// <summary>First failure in the current consecutive run; the "failing since" date.</summary>
    public DateTime? FirstFailureUtc { get; set; }

    /// <summary>Set when the breaker opened; null while closed.</summary>
    public DateTime? OpenedAtUtc { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// Account-level circuit breaker for Letterboxd authentication (issue #103).
/// After <see cref="Threshold"/> consecutive login failures for an account, the
/// breaker opens and all sync entry points skip that account as a no-op until
/// credentials are re-saved or a login succeeds. Only failures of the
/// authentication step itself are recorded; errors after a successful login
/// never touch the breaker. State persists in the plugin configurations
/// directory (same pattern as <see cref="SyncHistory"/>), never in
/// PluginConfiguration, so a stale dashboard save can't clobber it.
/// </summary>
public static class AuthBreaker
{
    public const int Threshold = 3;

    private static readonly object _lock = new();
    private static List<AuthBreakerEntry>? _entries;
    private static ILogger? _logger;

    /// <summary>Test-only hook for the state-file location. Production never assigns it.</summary>
    internal static string? DataPathOverride { get; set; }

    /// <summary>Test hook: drop the in-memory cache so the next access re-reads from disk.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _entries = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride))
                return DataPathOverride!;

            var assembly = typeof(AuthBreaker).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-auth-breaker.json");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "auth-breaker.json");

            return "auth-breaker.json";
        }
    }

    /// <summary>True when the account's breaker is open and logins must be skipped.</summary>
    public static bool IsOpen(string userJellyfinId, string letterboxdUsername)
    {
        lock (_lock)
        {
            return Find(userJellyfinId, letterboxdUsername)?.OpenedAtUtc != null;
        }
    }

    /// <summary>Snapshot of every account whose breaker is currently open (admin dashboard badge).</summary>
    public static List<AuthBreakerEntry> GetOpenEntries()
    {
        lock (_lock)
        {
            return Load().Where(e => e.OpenedAtUtc != null).Select(e => new AuthBreakerEntry
            {
                UserJellyfinId = e.UserJellyfinId,
                LetterboxdUsername = e.LetterboxdUsername,
                ConsecutiveFailures = e.ConsecutiveFailures,
                FirstFailureUtc = e.FirstFailureUtc,
                OpenedAtUtc = e.OpenedAtUtc,
                LastError = e.LastError
            }).ToList();
        }
    }

    /// <summary>Snapshot of the account's state, or null if it has never failed.</summary>
    public static AuthBreakerEntry? GetState(string userJellyfinId, string letterboxdUsername)
    {
        lock (_lock)
        {
            var e = Find(userJellyfinId, letterboxdUsername);
            if (e == null) return null;
            return new AuthBreakerEntry
            {
                UserJellyfinId = e.UserJellyfinId,
                LetterboxdUsername = e.LetterboxdUsername,
                ConsecutiveFailures = e.ConsecutiveFailures,
                FirstFailureUtc = e.FirstFailureUtc,
                OpenedAtUtc = e.OpenedAtUtc,
                LastError = e.LastError
            };
        }
    }

    /// <summary>
    /// Record an authentication failure. Returns true only when this call
    /// transitioned the breaker from closed to open, so the caller can raise
    /// the one-time admin notification.
    /// </summary>
    public static bool RecordFailure(string userJellyfinId, string letterboxdUsername, string? error)
    {
        lock (_lock)
        {
            var entries = Load();
            var e = Find(userJellyfinId, letterboxdUsername);
            if (e == null)
            {
                e = new AuthBreakerEntry { UserJellyfinId = userJellyfinId, LetterboxdUsername = letterboxdUsername };
                entries.Add(e);
            }

            var wasOpen = e.OpenedAtUtc != null;
            e.ConsecutiveFailures++;
            e.FirstFailureUtc ??= DateTime.UtcNow;
            e.LastError = error;
            if (!wasOpen && e.ConsecutiveFailures >= Threshold)
                e.OpenedAtUtc = DateTime.UtcNow;
            Save();

            var justOpened = !wasOpen && e.OpenedAtUtc != null;
            if (justOpened)
                _logger?.LogWarning(
                    "Auth breaker opened for Letterboxd account {Username}: {Count} consecutive login failures since {Since:u}",
                    letterboxdUsername, e.ConsecutiveFailures, e.FirstFailureUtc);
            return justOpened;
        }
    }

    /// <summary>A successful login clears the failure run and closes the breaker.</summary>
    public static void RecordSuccess(string userJellyfinId, string letterboxdUsername)
        => Clear(userJellyfinId, letterboxdUsername, "successful login");

    /// <summary>
    /// One-time admin notification for the closed→open transition, written to
    /// Jellyfin's activity log so it surfaces where operators actually look.
    /// Callers invoke this only when <see cref="RecordFailure"/> returned true.
    /// Null activityManager (tests, or DI not available) degrades to a no-op:
    /// the breaker itself still works, only the notification is skipped.
    /// </summary>
    public static async System.Threading.Tasks.Task NotifyOpenedAsync(
        MediaBrowser.Model.Activity.IActivityManager? activityManager,
        Guid jellyfinUserId,
        string letterboxdUsername,
        ILogger logger)
    {
        if (activityManager == null)
            return;

        var since = GetState(jellyfinUserId.ToString("N"), letterboxdUsername)?.FirstFailureUtc ?? DateTime.UtcNow;
        try
        {
            await activityManager.CreateAsync(new Jellyfin.Database.Implementations.Entities.ActivityLog(
                $"Jellyscribe: Letterboxd login for {letterboxdUsername} is failing; sync paused",
                "JellyscribeAuthBreakerOpened",
                jellyfinUserId)
            {
                ShortOverview = $"Login has been failing since {since:yyyy-MM-dd HH:mm} UTC.",
                Overview = $"Letterboxd login for account {letterboxdUsername} has been failing since {since:yyyy-MM-dd HH:mm} UTC. " +
                           "Syncing for this account is paused until its credentials are updated in Jellyscribe settings.",
                LogSeverity = Microsoft.Extensions.Logging.LogLevel.Warning
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to write auth-breaker activity log entry for {Username}: {Message}",
                letterboxdUsername, ex.Message);
        }
    }

    /// <summary>Credential re-save: close the breaker so the next run attempts login.</summary>
    public static void Reset(string userJellyfinId, string letterboxdUsername)
        => Clear(userJellyfinId, letterboxdUsername, "credentials re-saved");

    private static void Clear(string userJellyfinId, string letterboxdUsername, string reason)
    {
        lock (_lock)
        {
            var entries = Load();
            var e = Find(userJellyfinId, letterboxdUsername);
            if (e == null || (e.ConsecutiveFailures == 0 && e.OpenedAtUtc == null))
                return;

            if (e.OpenedAtUtc != null)
                _logger?.LogInformation("Auth breaker closed for Letterboxd account {Username} ({Reason})",
                    letterboxdUsername, reason);

            entries.Remove(e);
            Save();
        }
    }

    private static AuthBreakerEntry? Find(string userJellyfinId, string letterboxdUsername)
        => Load().FirstOrDefault(e =>
            e.UserJellyfinId == userJellyfinId &&
            string.Equals(e.LetterboxdUsername, letterboxdUsername, StringComparison.OrdinalIgnoreCase));

    private static List<AuthBreakerEntry> Load()
    {
        if (_entries != null) return _entries;

        _entries = new List<AuthBreakerEntry>();
        try
        {
            if (File.Exists(DataPath))
                _entries = JsonSerializer.Deserialize<List<AuthBreakerEntry>>(File.ReadAllText(DataPath))
                           ?? new List<AuthBreakerEntry>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load auth breaker state from {Path}", DataPath);
        }

        return _entries;
    }

    private static void Save()
    {
        try
        {
            var path = DataPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save auth breaker state to {Path}", DataPath);
        }
    }
}
