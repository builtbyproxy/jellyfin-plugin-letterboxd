using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Telemetry unit tests. Uses ControllerTestHarness for a fresh Plugin.Instance per
/// test (telemetry state persists inside PluginConfiguration). Lives in the "Plugin"
/// collection because TelemetryService reads Plugin.Instance statics.
/// </summary>
[Collection("Plugin")]
public class TelemetryServiceTests : IDisposable
{
    private readonly ControllerTestHarness _h;
    private readonly List<(string Url, string Json)> _sent = new();

    public TelemetryServiceTests()
    {
        _h = new ControllerTestHarness(currentUserId: TestIds.UserId);
        TelemetryService.ResetForTesting();
        TelemetryService.SenderOverride = (url, json) =>
        {
            _sent.Add((url, json));
            return Task.FromResult(true);
        };
    }

    public void Dispose()
    {
        TelemetryService.ResetForTesting();
        _h.Dispose();
    }

    private static class TestIds
    {
        public const string UserId = "11111111111111111111111111111111";
    }

    private TelemetryData Enable()
    {
        var t = _h.Config.Telemetry;
        t.Enabled = true;
        t.InstanceId = Guid.NewGuid().ToString();
        t.JitterMinutes = 0;
        return t;
    }

    private static SyncEvent Evt(SyncStatus status, string? error = null) => new()
    {
        FilmTitle = "Sinners", TmdbId = 1233413, Username = "lachlan",
        Timestamp = DateTime.UtcNow, Status = status, Error = error, Source = "test"
    };

    // ----- Classification -----

    [Theory]
    [InlineData("TMDb lookup returned 403 for /tmdb/123 after retries. Cloudflare is blocking.", TelemetryService.CatCloudflare)]
    [InlineData("Letterboxd returned 403 for sinners-2025. Likely anti-bot.", TelemetryService.CatCloudflare)]
    [InlineData("Letterboxd login error: bad credentials", TelemetryService.CatAuth)]
    [InlineData("Letterboxd returned 401 for sinners after re-authentication. Session may be permanently invalid.", TelemetryService.CatAuth)]
    [InlineData("returned 403 during login. Likely reCAPTCHA. Provide raw cookies instead.", TelemetryService.CatAuth)]
    [InlineData("Film with TMDb ID 123 not found on Letterboxd.", TelemetryService.CatTmdb)]
    [InlineData("Jellyseerr request errored", TelemetryService.CatJellyseerr)]
    [InlineData("something exploded", TelemetryService.CatOther)]
    [InlineData(null, TelemetryService.CatOther)]
    public void Classify_MapsKnownPatterns(string? message, string expected)
        => Assert.Equal(expected, TelemetryService.Classify(message));

    // ----- Buckets -----

    [Theory]
    [InlineData(0, "0")] [InlineData(1, "1")] [InlineData(2, "2-4")] [InlineData(4, "2-4")] [InlineData(5, "5+")]
    public void BucketAccounts_Edges(int n, string expected) => Assert.Equal(expected, TelemetryService.BucketAccounts(n));

    [Theory]
    [InlineData(0, "<500")] [InlineData(499, "<500")] [InlineData(500, "500-2k")] [InlineData(1999, "500-2k")]
    [InlineData(2000, "2k-10k")] [InlineData(9999, "2k-10k")] [InlineData(10000, "10k+")]
    public void BucketLibrary_Edges(int n, string expected) => Assert.Equal(expected, TelemetryService.BucketLibrary(n));

    [Theory]
    [InlineData(0, "0")] [InlineData(1, "1-10")] [InlineData(10, "1-10")] [InlineData(11, "11-100")]
    [InlineData(100, "11-100")] [InlineData(101, "100+")]
    public void BucketSyncs_Edges(int n, string expected) => Assert.Equal(expected, TelemetryService.BucketSyncs(n));

    // ----- Week gate -----

    [Fact]
    public void WeekStartUtc_IsMondayBased()
    {
        // 2026-06-10 is a Wednesday; its week starts Monday 2026-06-08.
        Assert.Equal(new DateTime(2026, 6, 8), TelemetryService.WeekStartUtc(new DateTime(2026, 6, 10, 15, 0, 0)));
        // A Monday maps to itself; a Sunday maps back six days.
        Assert.Equal(new DateTime(2026, 6, 8), TelemetryService.WeekStartUtc(new DateTime(2026, 6, 8, 0, 5, 0)));
        Assert.Equal(new DateTime(2026, 6, 8), TelemetryService.WeekStartUtc(new DateTime(2026, 6, 14, 23, 59, 0)));
    }

    [Fact]
    public async Task RunScheduled_SameWeek_DoesNotSend()
    {
        var t = Enable();
        // Last ping Monday 02:00; "now" is Sunday 14:00 of the SAME week, 6.5 days later.
        t.LastWeeklyPingUtc = new DateTime(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);
        TelemetryService.ClockOverride = () => new DateTime(2026, 6, 14, 14, 0, 0, DateTimeKind.Utc);

        await TelemetryService.RunScheduledAsync(libraryCount: 100, logger: null);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RunScheduled_WeekRolled_SendsAndResetsWindow()
    {
        var t = Enable();
        t.WindowSyncs = 12;
        t.WindowErrCloudflare = 3;
        t.LastWeeklyPingUtc = new DateTime(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc); // next Monday
        TelemetryService.ClockOverride = () => now;

        await TelemetryService.RunScheduledAsync(libraryCount: 1200, logger: null);

        var ping = Assert.Single(_sent);
        using var doc = JsonDocument.Parse(ping.Json);
        Assert.Equal("weekly", doc.RootElement.GetProperty("ping_type").GetString());
        Assert.Equal("11-100", doc.RootElement.GetProperty("buckets").GetProperty("syncs_per_week").GetString());
        Assert.Equal("500-2k", doc.RootElement.GetProperty("buckets").GetProperty("library").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("errors").GetProperty("cloudflare_403").GetInt32());
        // Window resets after a successful send.
        Assert.Equal(0, t.WindowSyncs);
        Assert.Equal(0, t.WindowErrCloudflare);
        Assert.Equal(now, t.LastWeeklyPingUtc);
    }

    [Fact]
    public async Task RunScheduled_JitterNotPassed_DoesNotSend()
    {
        var t = Enable();
        t.JitterMinutes = 600; // 10:00 UTC
        t.LastWeeklyPingUtc = new DateTime(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);
        TelemetryService.ClockOverride = () => new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc); // rolled, but 04:00

        await TelemetryService.RunScheduledAsync(libraryCount: 1, logger: null);

        Assert.Empty(_sent);
    }

    // ----- Disabled = dead code -----

    [Fact]
    public async Task Disabled_NothingCountsNothingSends()
    {
        // Default config: Enabled=false.
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Success));
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Cloudflare is blocking"));
        TelemetryService.RecordError(TelemetryService.CatAuth);
        await TelemetryService.RunScheduledAsync(libraryCount: 1, logger: null);

        Assert.Empty(_sent);
        Assert.Equal(0, _h.Config.Telemetry.WindowSyncs);
        Assert.Equal(0, _h.Config.Telemetry.WindowErrCloudflare);
        Assert.False(_h.Config.Telemetry.StateAuth);
    }

    // ----- Counting + recovery -----

    [Fact]
    public void SyncEvents_CountIntoPersistedConfig()
    {
        var t = Enable();
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Success));
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Rewatch));
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Skipped));

        // Counters live on the PluginConfiguration object, i.e. they survive restarts
        // by construction (config persistence), not in TelemetryService memory.
        Assert.Equal(2, t.WindowSyncs);
        Assert.Equal(1, t.WindowSkipped);
    }

    [Fact]
    public void Success_ClearsErrorStates()
    {
        var t = Enable();
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Cloudflare is blocking"));
        Assert.True(t.StateCloudflare);

        TelemetryService.OnSyncEvent(Evt(SyncStatus.Success));
        Assert.False(t.StateCloudflare);
    }

    // ----- Rising edge + daily cap + consolidation -----

    [Fact]
    public void FirstFailure_FiresTransitionPing_SecondSameCategoryDoesNot()
    {
        Enable();
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Cloudflare is blocking"));
        Assert.Single(_sent);
        Assert.Contains("error_transition", _sent[0].Json);

        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Cloudflare is blocking"));
        Assert.Single(_sent); // still failing, no new rising edge
    }

    [Fact]
    public void SecondCategorySameDay_QueuesInsteadOfSending()
    {
        var t = Enable();
        var now = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);
        TelemetryService.ClockOverride = () => now;

        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Cloudflare is blocking"));
        Assert.Single(_sent);

        now = now.AddHours(5); // 14:00 same day, cap spent
        TelemetryService.OnSyncEvent(Evt(SyncStatus.Failed, "Film with TMDb ID 1 not found on Letterboxd."));

        Assert.Single(_sent);          // not sent...
        Assert.True(t.TransitionQueued); // ...queued instead, never dropped
        Assert.True(t.StateTmdb);
    }

    [Fact]
    public async Task QueuedTransition_DrainsWhenCapReopens()
    {
        var t = Enable();
        t.StateCloudflare = true;
        t.StateTmdb = true;
        t.TransitionQueued = true;
        t.LastTransitionPingUtc = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);
        t.LastWeeklyPingUtc = new DateTime(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc); // same week → no weekly
        TelemetryService.ClockOverride = () => new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc); // >24h later

        await TelemetryService.RunScheduledAsync(libraryCount: 1, logger: null);

        var ping = Assert.Single(_sent);
        using var doc = JsonDocument.Parse(ping.Json);
        Assert.Equal("error_transition", doc.RootElement.GetProperty("ping_type").GetString());
        // Consolidated ping carries the FULL current error-state map.
        var state = doc.RootElement.GetProperty("errors").GetProperty("state");
        Assert.True(state.GetProperty("cloudflare_403").GetBoolean());
        Assert.True(state.GetProperty("tmdb_lookup").GetBoolean());
        Assert.False(t.TransitionQueued);
    }

    // ----- Payload anonymity + shape -----

    [Fact]
    public void Payload_BucketsOnly_NoIdentifiersBeyondInstanceId()
    {
        var t = Enable();
        _h.AddAccount(TestIds.UserId, "my-lb-username", watchlistSync: true);
        t.WindowSyncs = 37;

        var json = TelemetryService.BuildPayload("weekly", libraryCount: 1234);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(t.InstanceId, root.GetProperty("instance_id").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("features").GetProperty("watchlist_sync").GetBoolean());
        Assert.Equal("1", root.GetProperty("buckets").GetProperty("accounts").GetString());
        Assert.Equal("500-2k", root.GetProperty("buckets").GetProperty("library").GetString());
        Assert.Equal("11-100", root.GetProperty("buckets").GetProperty("syncs_per_week").GetString());

        // The promise, mechanically enforced: no usernames, no raw counts.
        Assert.DoesNotContain("my-lb-username", json);
        Assert.DoesNotContain("lachlan", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234", json);
        Assert.DoesNotContain("37", json.Replace("schema_version", ""));
    }

    // ----- Preview endpoint policy -----

    [Fact]
    public void PreviewEndpoint_RequiresElevation()
    {
        var method = typeof(LetterboxdController).GetMethod("GetTelemetryPreview", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("RequiresElevation", attr!.Policy);
    }
}
