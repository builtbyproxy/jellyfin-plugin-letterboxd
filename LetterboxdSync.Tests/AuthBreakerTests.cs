using System;
using System.IO;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class AuthBreakerTests : IDisposable
{
    private readonly string _path;

    public AuthBreakerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "lbs-breaker-" + Guid.NewGuid().ToString("N") + ".json");
        AuthBreaker.DataPathOverride = _path;
        AuthBreaker.ResetForTesting();
    }

    public void Dispose()
    {
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void ThirdConsecutiveFailure_OpensBreakerExactlyOnce()
    {
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));

        Assert.True(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        // Further failures while open must not report the transition again.
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
    }

    [Fact]
    public void OpenedState_RecordsFirstFailureDate()
    {
        var before = DateTime.UtcNow;
        AuthBreaker.RecordFailure("u1", "kostadamus", "e1");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e2");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e3");

        var state = AuthBreaker.GetState("u1", "kostadamus");
        Assert.NotNull(state);
        Assert.Equal(3, state!.ConsecutiveFailures);
        Assert.NotNull(state.OpenedAtUtc);
        Assert.NotNull(state.FirstFailureUtc);
        Assert.True(state.FirstFailureUtc >= before.AddSeconds(-1));
        Assert.Equal("e3", state.LastError);
    }

    [Fact]
    public void SuccessResetsCount_BeforeThreshold()
    {
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        AuthBreaker.RecordSuccess("u1", "kostadamus");

        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Null(AuthBreaker.GetState("u1", "kostadamus"));

        // The count restarted: two more failures must not open it.
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "e"));
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
    }

    [Fact]
    public void Reset_ClosesAnOpenBreaker()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        AuthBreaker.Reset("u1", "kostadamus");
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Null(AuthBreaker.GetState("u1", "kostadamus"));
    }

    [Fact]
    public void State_SurvivesReload()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        // Simulate a restart: drop the in-memory cache, forcing a re-read from disk.
        AuthBreaker.ResetForTesting();
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Equal(3, AuthBreaker.GetState("u1", "kostadamus")!.ConsecutiveFailures);
    }

    [Fact]
    public void Accounts_AreIsolated()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");

        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.False(AuthBreaker.IsOpen("u1", "otheraccount"));
        Assert.False(AuthBreaker.IsOpen("u2", "kostadamus"));

        AuthBreaker.RecordFailure("u2", "kostadamus", "e");
        AuthBreaker.Reset("u1", "kostadamus");
        Assert.Equal(1, AuthBreaker.GetState("u2", "kostadamus")!.ConsecutiveFailures);
    }

    [Fact]
    public void UsernameMatch_IsCaseInsensitive()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "Kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        AuthBreaker.Reset("u1", "KOSTADAMUS");
        Assert.False(AuthBreaker.IsOpen("u1", "Kostadamus"));
    }
}
