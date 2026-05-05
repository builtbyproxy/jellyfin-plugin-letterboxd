using System.Reflection;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// SyncProgress is a static singleton that tracks long-running sync state for the
/// dashboard. Because it's static, every test resets state via Start() before
/// asserting, which is the same pattern the real callers use at the top of each run.
/// </summary>
[Collection("SyncProgress")]
public class SyncProgressTests
{
    [Fact]
    public void Start_SetsTaskNamePhaseAndRunningFlag()
    {
        SyncProgress.Start("MyTask", "init");

        Assert.Equal("MyTask", SyncProgress.TaskName);
        Assert.Equal("init", SyncProgress.Phase);
        Assert.True(SyncProgress.IsRunning);
        Assert.NotNull(SyncProgress.StartedAt);
    }

    [Fact]
    public void Start_ResetsCounters()
    {
        SyncProgress.Start("First", "p");
        SyncProgress.SetTotal(10);
        SyncProgress.IncrementProcessed();
        SyncProgress.IncrementCacheHit();
        SyncProgress.IncrementNewLookup();

        SyncProgress.Start("Second", "p2");

        Assert.Equal(0, SyncProgress.TotalItems);
        Assert.Equal(0, SyncProgress.ProcessedItems);
        Assert.Equal(0, SyncProgress.CacheHits);
        Assert.Equal(0, SyncProgress.NewLookups);
        Assert.Equal("Second", SyncProgress.TaskName);
    }

    [Fact]
    public void SetPhase_UpdatesPhaseWithoutTouchingOtherFields()
    {
        SyncProgress.Start("Task", "scanning");
        SyncProgress.SetTotal(42);
        SyncProgress.IncrementProcessed();

        SyncProgress.SetPhase("uploading");

        Assert.Equal("uploading", SyncProgress.Phase);
        Assert.Equal(42, SyncProgress.TotalItems);
        Assert.Equal(1, SyncProgress.ProcessedItems);
    }

    [Fact]
    public void SetTotal_OverwritesPreviousValue()
    {
        SyncProgress.Start("Task", "p");
        SyncProgress.SetTotal(5);
        SyncProgress.SetTotal(50);

        Assert.Equal(50, SyncProgress.TotalItems);
    }

    [Fact]
    public void IncrementProcessed_AccumulatesMonotonically()
    {
        SyncProgress.Start("Task", "p");

        for (int i = 0; i < 7; i++) SyncProgress.IncrementProcessed();

        Assert.Equal(7, SyncProgress.ProcessedItems);
    }

    [Fact]
    public void IncrementCacheHit_AndNewLookup_TrackedSeparately()
    {
        SyncProgress.Start("Task", "p");

        SyncProgress.IncrementCacheHit();
        SyncProgress.IncrementCacheHit();
        SyncProgress.IncrementNewLookup();

        Assert.Equal(2, SyncProgress.CacheHits);
        Assert.Equal(1, SyncProgress.NewLookups);
    }

    [Fact]
    public void Complete_ClearsRunningFlag_AndSetsPhaseComplete()
    {
        SyncProgress.Start("Task", "active");

        SyncProgress.Complete();

        Assert.False(SyncProgress.IsRunning);
        Assert.Equal("complete", SyncProgress.Phase);
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        SyncProgress.Start("SnapshotTask", "phase-x");
        SyncProgress.SetTotal(10);
        SyncProgress.IncrementProcessed();
        SyncProgress.IncrementCacheHit();

        var snapshot = SyncProgress.GetSnapshot();
        var t = snapshot.GetType();

        // Anonymous type, so fields need reflection. Verify the dashboard payload contract.
        Assert.Equal("SnapshotTask", t.GetProperty("taskName")!.GetValue(snapshot));
        Assert.Equal("phase-x", t.GetProperty("phase")!.GetValue(snapshot));
        Assert.Equal(10, t.GetProperty("totalItems")!.GetValue(snapshot));
        Assert.Equal(1, t.GetProperty("processedItems")!.GetValue(snapshot));
        Assert.Equal(1, t.GetProperty("cacheHits")!.GetValue(snapshot));
        Assert.Equal(0, t.GetProperty("newLookups")!.GetValue(snapshot));
        Assert.Equal(true, t.GetProperty("isRunning")!.GetValue(snapshot));
        Assert.NotNull(t.GetProperty("startedAt")!.GetValue(snapshot));
        // Elapsed is integer seconds; for a freshly-started run it's ~0 but never negative.
        var elapsed = (int)t.GetProperty("elapsedSeconds")!.GetValue(snapshot)!;
        Assert.True(elapsed >= 0);
    }

    [Fact]
    public void GetSnapshot_AfterComplete_ReportsNotRunning()
    {
        SyncProgress.Start("Task", "p");
        SyncProgress.Complete();

        var snapshot = SyncProgress.GetSnapshot();
        var t = snapshot.GetType();

        Assert.Equal(false, t.GetProperty("isRunning")!.GetValue(snapshot));
        Assert.Equal("complete", t.GetProperty("phase")!.GetValue(snapshot));
    }
}
