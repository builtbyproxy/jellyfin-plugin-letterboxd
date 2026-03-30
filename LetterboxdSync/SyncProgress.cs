using System;

namespace LetterboxdSync;

/// <summary>
/// Tracks the progress of long-running sync operations for dashboard display.
/// </summary>
public static class SyncProgress
{
    private static readonly object _lock = new();

    public static string? TaskName { get; private set; }
    public static string? Phase { get; private set; }
    public static int TotalItems { get; private set; }
    public static int ProcessedItems { get; private set; }
    public static int CacheHits { get; private set; }
    public static int NewLookups { get; private set; }
    public static bool IsRunning { get; private set; }
    public static DateTime? StartedAt { get; private set; }

    public static void Start(string taskName, string phase)
    {
        lock (_lock)
        {
            TaskName = taskName;
            Phase = phase;
            TotalItems = 0;
            ProcessedItems = 0;
            CacheHits = 0;
            NewLookups = 0;
            IsRunning = true;
            StartedAt = DateTime.UtcNow;
        }
    }

    public static void SetPhase(string phase)
    {
        lock (_lock) { Phase = phase; }
    }

    public static void SetTotal(int total)
    {
        lock (_lock) { TotalItems = total; }
    }

    public static void IncrementProcessed()
    {
        lock (_lock) { ProcessedItems++; }
    }

    public static void IncrementCacheHit()
    {
        lock (_lock) { CacheHits++; }
    }

    public static void IncrementNewLookup()
    {
        lock (_lock) { NewLookups++; }
    }

    public static void Complete()
    {
        lock (_lock)
        {
            Phase = "complete";
            IsRunning = false;
        }
    }

    public static object GetSnapshot()
    {
        lock (_lock)
        {
            return new
            {
                taskName = TaskName,
                phase = Phase,
                totalItems = TotalItems,
                processedItems = ProcessedItems,
                cacheHits = CacheHits,
                newLookups = NewLookups,
                isRunning = IsRunning,
                startedAt = StartedAt,
                elapsedSeconds = StartedAt.HasValue ? (int)(DateTime.UtcNow - StartedAt.Value).TotalSeconds : 0
            };
        }
    }
}
