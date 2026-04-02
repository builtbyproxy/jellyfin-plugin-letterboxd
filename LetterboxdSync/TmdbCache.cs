using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Persistent cache of Letterboxd film slug → TMDb ID mappings.
/// Eliminates repeated HTTP requests for films already resolved.
/// </summary>
public static class TmdbCache
{
    private static readonly object _lock = new();
    private static Dictionary<string, int>? _cache;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string CachePath
    {
        get
        {
            var assembly = typeof(TmdbCache).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-tmdb-cache.json");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "tmdb-cache.json");

            return "tmdb-cache.json";
        }
    }

    private static Dictionary<string, int> Load()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
                _logger?.LogInformation("Loaded {Count} slug→TMDb mappings from cache", _cache.Count);
                return _cache;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load TMDb cache from {Path}", CachePath);
        }

        _cache = new Dictionary<string, int>();
        return _cache;
    }

    private static void Save()
    {
        try
        {
            var path = CachePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save TMDb cache to {Path}", CachePath);
        }
    }

    public static int? Get(string slug)
    {
        lock (_lock)
        {
            var cache = Load();
            return cache.TryGetValue(slug, out var tmdbId) ? tmdbId : null;
        }
    }

    public static void Set(string slug, int tmdbId)
    {
        lock (_lock)
        {
            var cache = Load();
            cache[slug] = tmdbId;
            Save();
        }
    }

    public static int Count
    {
        get
        {
            lock (_lock)
            {
                return Load().Count;
            }
        }
    }
}
