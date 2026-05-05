using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LetterboxdSync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// TmdbCache is a static singleton with file persistence; tests serialise via the
/// xUnit collection so the override path and in-memory cache don't bleed across.
/// Each test sets a unique CachePathOverride and resets the in-memory cache.
/// </summary>
[Collection("TmdbCache")]
public class TmdbCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cachePath;

    public TmdbCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-tmdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cachePath = Path.Combine(_tempDir, "tmdb-cache.json");
        TmdbCache.CachePathOverride = _cachePath;
        TmdbCache.ResetForTesting();
        TmdbCache.SetLogger(NullLogger.Instance);
    }

    public void Dispose()
    {
        TmdbCache.CachePathOverride = null;
        TmdbCache.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Get_BeforeAnySet_ReturnsNull()
    {
        Assert.Null(TmdbCache.Get("nonexistent-slug"));
    }

    [Fact]
    public void Set_ThenGet_ReturnsStoredValue()
    {
        TmdbCache.Set("sinners-2025", 1233413);

        Assert.Equal(1233413, TmdbCache.Get("sinners-2025"));
    }

    [Fact]
    public void Set_WritesToCacheFile()
    {
        TmdbCache.Set("iron-man", 1726);

        Assert.True(File.Exists(_cachePath));
        var json = File.ReadAllText(_cachePath);
        var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        Assert.NotNull(dict);
        Assert.Equal(1726, dict!["iron-man"]);
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        TmdbCache.Set("luca", 100);
        TmdbCache.Set("luca", 200);

        Assert.Equal(200, TmdbCache.Get("luca"));
    }

    [Fact]
    public void Count_ReflectsNumberOfMappings()
    {
        TmdbCache.Set("a", 1);
        TmdbCache.Set("b", 2);
        TmdbCache.Set("c", 3);

        Assert.Equal(3, TmdbCache.Count);
    }

    [Fact]
    public void Set_DuplicateKey_DoesNotIncrementCount()
    {
        TmdbCache.Set("a", 1);
        TmdbCache.Set("a", 2);
        TmdbCache.Set("a", 3);

        Assert.Equal(1, TmdbCache.Count);
    }

    [Fact]
    public void Load_FromExistingFile_PopulatesCache()
    {
        // Pre-seed the file before any access; the cache should load it on first read.
        var seedDict = new Dictionary<string, int> { ["pre-existing"] = 999 };
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(seedDict));
        TmdbCache.ResetForTesting(); // force re-load

        Assert.Equal(999, TmdbCache.Get("pre-existing"));
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        // CachePathOverride points at a non-existent file; cache should silently start empty.
        Assert.Equal(0, TmdbCache.Count);
    }

    [Fact]
    public void Load_MalformedFile_DoesNotThrow_StartsEmpty()
    {
        File.WriteAllText(_cachePath, "{ this is not valid json");
        TmdbCache.ResetForTesting();

        // Loader catches parse exceptions and falls back to an empty cache.
        Assert.Equal(0, TmdbCache.Count);
        Assert.Null(TmdbCache.Get("anything"));
    }

    [Fact]
    public void Set_ConcurrentCalls_DoNotCorruptCache()
    {
        // The cache is locked, so parallel writes shouldn't lose entries.
        System.Threading.Tasks.Parallel.For(0, 50, i =>
        {
            TmdbCache.Set($"film-{i}", i + 1000);
        });

        Assert.Equal(50, TmdbCache.Count);
        Assert.Equal(1042, TmdbCache.Get("film-42"));
    }

    [Fact]
    public void Get_AfterSet_PersistsAcrossInMemoryReset()
    {
        // Persistence sanity check: after a reset (process restart equivalent),
        // a previously-stored entry should still be retrievable from disk.
        TmdbCache.Set("oppenheimer", 872585);
        TmdbCache.ResetForTesting();

        Assert.Equal(872585, TmdbCache.Get("oppenheimer"));
    }
}
