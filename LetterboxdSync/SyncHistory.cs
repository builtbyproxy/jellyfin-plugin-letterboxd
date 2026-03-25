using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

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
    public string? Source { get; set; } // "playback" or "scheduled"
}

public static class SyncHistory
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private const int MaxEvents = 500;

    private static string DataPath
    {
        get
        {
            // Store in the configurations directory — survives version upgrades
            // This is where LetterboxdSync.xml lives: /config/data/plugins/configurations/
            var assembly = typeof(SyncHistory).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-sync-history.json");
            }

            // Fallback: next to the DLL
            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "sync-history.json");

            return "sync-history.json";
        }
    }

    private static List<SyncEvent> LoadEvents()
    {
        if (_events != null) return _events;

        try
        {
            if (File.Exists(DataPath))
            {
                var json = File.ReadAllText(DataPath);
                _events = JsonSerializer.Deserialize<List<SyncEvent>>(json) ?? new List<SyncEvent>();
                return _events;
            }
        }
        catch { }

        _events = new List<SyncEvent>();
        return _events;
    }

    private static void SaveEvents()
    {
        try
        {
            var path = DataPath;
            Console.WriteLine($"[LetterboxdSync] Saving sync history to: {path}");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Console.WriteLine($"[LetterboxdSync] Saved {_events?.Count ?? 0} events to sync history");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LetterboxdSync] Failed to save sync history: {ex.Message}");
        }
    }

    public static void Record(SyncEvent evt)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            events.Insert(0, evt);

            // Trim to max
            if (events.Count > MaxEvents)
                events.RemoveRange(MaxEvents, events.Count - MaxEvents);

            SaveEvents();
        }
    }

    public static List<SyncEvent> GetRecent(int count = 100)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            return events.GetRange(0, Math.Min(count, events.Count));
        }
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches) GetStats()
    {
        lock (_lock)
        {
            var events = LoadEvents();
            return (
                events.Count,
                events.FindAll(e => e.Status == SyncStatus.Success).Count,
                events.FindAll(e => e.Status == SyncStatus.Failed).Count,
                events.FindAll(e => e.Status == SyncStatus.Skipped).Count,
                events.FindAll(e => e.Status == SyncStatus.Rewatch).Count
            );
        }
    }
}
