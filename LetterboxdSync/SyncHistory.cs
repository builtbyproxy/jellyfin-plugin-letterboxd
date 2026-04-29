using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public enum SyncStatus
{
    Success,
    Skipped,
    Failed,
    Rewatch
}

public class SyncEvent
{
    public string FilmTitle { get; set; } = string.Empty;
    public string FilmSlug { get; set; } = string.Empty;
    public int TmdbId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public DateTime? ViewingDate { get; set; }
    public SyncStatus Status { get; set; }
    public string? Error { get; set; }
    public string? Source { get; set; }
}

public static class SyncHistory
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string DataPath
    {
        get
        {
            var assembly = typeof(SyncHistory).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-sync-history.jsonl");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "sync-history.jsonl");

            return "sync-history.jsonl";
        }
    }

    private static List<SyncEvent> LoadEvents()
    {
        if (_events != null) return _events;

        _events = new List<SyncEvent>();

        try
        {
            var jsonlPath = DataPath;
            if (File.Exists(jsonlPath))
            {
                foreach (var line in File.ReadLines(jsonlPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var evt = JsonSerializer.Deserialize<SyncEvent>(line);
                        if (evt != null) _events.Add(evt);
                    }
                    catch { }
                }
                return _events;
            }

            // Migrate from old JSON format if it exists
            var legacyPath = jsonlPath.Replace(".jsonl", ".json");
            if (File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                _events = JsonSerializer.Deserialize<List<SyncEvent>>(json) ?? new List<SyncEvent>();
                // Write in new JSONL format
                SaveAllEvents();
                _logger?.LogInformation("Migrated {Count} sync history events from JSON to JSONL", _events.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load sync history from {Path}", DataPath);
        }

        return _events;
    }

    private static void SaveAllEvents()
    {
        try
        {
            var path = DataPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var writer = new StreamWriter(path, append: false);
            foreach (var evt in _events!)
            {
                writer.WriteLine(JsonSerializer.Serialize(evt));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save sync history to {Path}", DataPath);
        }
    }

    public static void Record(SyncEvent evt)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            events.Add(evt);

            try
            {
                var path = DataPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, JsonSerializer.Serialize(evt) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append sync event to {Path}", DataPath);
            }
        }
    }

    public static List<SyncEvent> GetRecent(int count = 100, string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
                filtered = filtered.Where(e => e.Username == username);

            return filtered.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }
    }

    /// <summary>
    /// Most recent status recorded for this user/film, or null if there's no history.
    /// Used to prioritise previously-failed films at the head of the sync queue.
    /// </summary>
    public static SyncStatus? GetLastStatusForFilm(string username, int tmdbId)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            SyncEvent? latest = null;
            foreach (var e in events)
            {
                if (e.TmdbId != tmdbId) continue;
                if (!string.Equals(e.Username, username, StringComparison.Ordinal)) continue;
                if (latest == null || e.Timestamp > latest.Timestamp) latest = e;
            }
            return latest?.Status;
        }
    }

    /// <summary>
    /// True if we have a Success or Rewatch entry for this user/film combo whose ViewingDate
    /// matches the current viewing date. Used to short-circuit the duplicate check without
    /// making an HTTP call to Letterboxd.
    /// </summary>
    public static bool WasSuccessfullySynced(string username, int tmdbId, DateTime viewingDate)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            var target = viewingDate.Date;
            foreach (var e in events)
            {
                if (e.TmdbId != tmdbId) continue;
                if (!string.Equals(e.Username, username, StringComparison.Ordinal)) continue;
                if (e.Status != SyncStatus.Success && e.Status != SyncStatus.Rewatch) continue;
                if (e.ViewingDate?.Date == target) return true;
            }
            return false;
        }
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches) GetStats(string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
                filtered = filtered.Where(e => e.Username == username);

            var list = filtered.ToList();
            return (
                list.Count,
                list.Count(e => e.Status == SyncStatus.Success),
                list.Count(e => e.Status == SyncStatus.Failed),
                list.Count(e => e.Status == SyncStatus.Skipped),
                list.Count(e => e.Status == SyncStatus.Rewatch)
            );
        }
    }
}
