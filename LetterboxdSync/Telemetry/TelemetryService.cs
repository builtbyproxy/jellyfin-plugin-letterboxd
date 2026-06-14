using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Central telemetry brain: counts sync outcomes, classifies errors, detects rising-edge
/// error transitions, builds the anonymous payload, and sends pings. Static (like
/// <see cref="TmdbCache"/>/<see cref="SyncGate"/>) because it is cross-cutting state fed
/// from runners, the playback handler, and scheduled tasks alike.
///
/// Hard rules enforced here:
///  - Every entry point is a no-op while telemetry is disabled or Plugin.Instance is null.
///  - Nothing in here may ever throw into a sync path; telemetry failure is always silent.
///  - The payload contains no IPs, usernames, titles, or raw counts — buckets only.
/// </summary>
internal static class TelemetryService
{
    public const string CatCloudflare = "cloudflare_403";
    public const string CatAuth = "auth_failure";
    public const string CatTmdb = "tmdb_lookup";
    public const string CatJellyseerr = "jellyseerr_error";
    public const string CatOther = "other";

    private static readonly object _lock = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static DateTime _lastSaveUtc = DateTime.MinValue;

    /// <summary>Test seam: replaces the HTTP send. Args: (url, jsonBody) → success.</summary>
    internal static Func<string, string, Task<bool>>? SenderOverride { get; set; }

    /// <summary>Test seam: replaces DateTime.UtcNow.</summary>
    internal static Func<DateTime>? ClockOverride { get; set; }

    private static DateTime UtcNow => ClockOverride?.Invoke() ?? DateTime.UtcNow;

    private static TelemetryData? Data => Plugin.Instance?.Configuration?.Telemetry;

    private static bool Enabled => Data is { Enabled: true } d && !string.IsNullOrEmpty(d.InstanceId);

    // ---------- Classification ----------

    /// <summary>
    /// Maps a failure message onto one of the five error categories. Order matters:
    /// auth indicators are checked before the generic 403/Cloudflare bucket because
    /// "401 after re-authentication" and login errors are auth problems even when
    /// Cloudflare vocabulary appears alongside.
    /// </summary>
    public static string Classify(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return CatOther;
        var m = errorMessage;

        if (Contains(m, "login error") || Contains(m, "401") || Contains(m, "reCAPTCHA")
            || Contains(m, "Auth failed") || Contains(m, "session may be permanently invalid"))
            return CatAuth;

        if (Contains(m, "Jellyseerr"))
            return CatJellyseerr;

        if (Contains(m, "Cloudflare") || Contains(m, "anti-bot") || Contains(m, "403"))
            return CatCloudflare;

        if (Contains(m, "not found on Letterboxd") || Contains(m, "TMDb lookup") || Contains(m, "TMDb ID"))
            return CatTmdb;

        return CatOther;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---------- Recording (called from sync paths; never throws) ----------

    /// <summary>
    /// Chokepoint hook called from SyncHistory.Record: every sync outcome in the plugin
    /// flows through there, so this one hook counts successes, skips, and failures from
    /// the runner, the playback handler, and diary import alike.
    /// </summary>
    public static void OnSyncEvent(SyncEvent e)
    {
        try
        {
            if (!Enabled) return;
            lock (_lock)
            {
                var d = Data!;
                switch (e.Status)
                {
                    case SyncStatus.Success:
                    case SyncStatus.Rewatch:
                        d.WindowSyncs++;
                        // Recovery: a film made it all the way to Letterboxd, so the
                        // pipeline is healthy; flip every category back to clean. The
                        // recovery is visible in the next weekly payload (by design,
                        // no recovery ping).
                        d.StateCloudflare = d.StateAuth = d.StateTmdb = d.StateOther = false;
                        break;
                    case SyncStatus.Skipped:
                        d.WindowSkipped++;
                        break;
                    case SyncStatus.Failed:
                        RecordErrorLocked(d, Classify(e.Error));
                        break;
                }
                SaveIfDueLocked();
            }
        }
        catch { /* telemetry must never break a sync */ }
    }

    /// <summary>Direct error hook for failure paths that don't record a SyncEvent (auth catches, Jellyseerr aggregates).</summary>
    public static void RecordError(string category)
    {
        try
        {
            if (!Enabled) return;
            lock (_lock)
            {
                RecordErrorLocked(Data!, category);
                SaveIfDueLocked();
            }
        }
        catch { /* never throws into callers */ }
    }

    private static void RecordErrorLocked(TelemetryData d, string category)
    {
        bool wasFailing;
        switch (category)
        {
            case CatCloudflare: wasFailing = d.StateCloudflare; d.StateCloudflare = true; d.WindowErrCloudflare++; break;
            case CatAuth: wasFailing = d.StateAuth; d.StateAuth = true; d.WindowErrAuth++; break;
            case CatTmdb: wasFailing = d.StateTmdb; d.StateTmdb = true; d.WindowErrTmdb++; break;
            case CatJellyseerr: wasFailing = d.StateJellyseerr; d.StateJellyseerr = true; d.WindowErrJellyseerr++; break;
            default: wasFailing = d.StateOther; d.StateOther = true; d.WindowErrOther++; break;
        }

        if (wasFailing) return; // not a rising edge

        // Rising edge: send a transition ping now if the daily cap allows, else queue one
        // consolidated ping for when the window reopens. Queued, never dropped client-side.
        if (d.LastTransitionPingUtc == null || UtcNow - d.LastTransitionPingUtc >= TimeSpan.FromDays(1))
        {
            d.LastTransitionPingUtc = UtcNow;
            d.TransitionQueued = false;
            SaveLocked();
            _ = SendAsync(BuildPayloadLocked(d, "error_transition", libraryCount: null));
        }
        else
        {
            d.TransitionQueued = true;
            SaveLocked();
        }
    }

    // ---------- Scheduled run (weekly ping + queued-transition drain) ----------

    /// <summary>
    /// Called by the daily TelemetryTask. Sends the weekly ping when the UTC week has
    /// rolled over since the last successful ping AND the per-instance jitter minute has
    /// passed; drains a queued transition ping when the daily cap has reopened.
    /// </summary>
    public static async Task RunScheduledAsync(int? libraryCount, ILogger? logger)
    {
        try
        {
            if (!Enabled) return;

            string? weeklyJson = null;
            string? transitionJson = null;

            lock (_lock)
            {
                var d = Data!;
                var now = UtcNow;

                if (d.TransitionQueued
                    && (d.LastTransitionPingUtc == null || now - d.LastTransitionPingUtc >= TimeSpan.FromDays(1)))
                {
                    d.LastTransitionPingUtc = now;
                    d.TransitionQueued = false;
                    transitionJson = BuildPayloadLocked(d, "error_transition", libraryCount);
                }

                var weekRolled = d.LastWeeklyPingUtc == null
                    || WeekStartUtc(now) > WeekStartUtc(d.LastWeeklyPingUtc.Value);
                var jitterPassed = now.TimeOfDay >= TimeSpan.FromMinutes(d.JitterMinutes);

                if (weekRolled && jitterPassed)
                    weeklyJson = BuildPayloadLocked(d, "weekly", libraryCount);
            }

            if (transitionJson != null)
            {
                await SendAsync(transitionJson).ConfigureAwait(false);
                lock (_lock) SaveLocked();
            }

            if (weeklyJson != null && await SendAsync(weeklyJson).ConfigureAwait(false))
            {
                lock (_lock)
                {
                    var d = Data!;
                    d.LastWeeklyPingUtc = UtcNow;
                    d.WindowSyncs = d.WindowSkipped = 0;
                    d.WindowErrCloudflare = d.WindowErrAuth = d.WindowErrTmdb =
                        d.WindowErrJellyseerr = d.WindowErrOther = 0;
                    SaveLocked();
                }
                logger?.LogInformation("Telemetry: weekly ping sent");
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug("Telemetry scheduled run failed (non-fatal): {Message}", ex.Message);
        }
    }

    /// <summary>UTC Monday 00:00 of the week containing <paramref name="utc"/> — matches the ingest function's week computation.</summary>
    internal static DateTime WeekStartUtc(DateTime utc)
        => utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7));

    // ---------- Payload ----------

    /// <summary>
    /// Builds the exact JSON that is (or would be) sent. Bucketed counts only, no raw
    /// numbers; feature booleans are "any account has it on". This same method backs the
    /// settings-page preview, so what users see is what gets sent, field for field.
    /// </summary>
    public static string BuildPayload(string pingType, int? libraryCount)
    {
        lock (_lock)
        {
            var d = Data ?? new TelemetryData();
            return BuildPayloadLocked(d, pingType, libraryCount);
        }
    }

    private static string BuildPayloadLocked(TelemetryData d, string pingType, int? libraryCount)
    {
        var cfg = Plugin.Instance?.Configuration;
        var accounts = cfg?.Accounts ?? new List<Account>();
        var enabled = accounts.Where(a => a.Enabled).ToList();

        var payload = new
        {
            schema_version = TelemetryConstants.SchemaVersion,
            instance_id = d.InstanceId,
            ping_type = pingType,
            plugin_version = Plugin.Instance?.Version?.ToString() ?? "unknown",
            jellyfin_version = JellyfinVersion ?? "unknown",
            features = new
            {
                multi_account = enabled.Count > 1,
                sync_favorites = enabled.Any(a => a.SyncFavorites),
                date_filter = enabled.Any(a => a.EnableDateFilter),
                watchlist_sync = enabled.Any(a => a.EnableWatchlistSync),
                diary_import = enabled.Any(a => a.EnableDiaryImport),
                auto_request = enabled.Any(a => a.AutoRequestWatchlist),
                backfill_requests = enabled.Any(a => a.BackfillAvailableRequests),
                mirror_jellyseerr = enabled.Any(a => a.MirrorJellyseerrWatchlist),
                skip_previously_synced = enabled.Any(a => a.SkipPreviouslySynced),
                stop_on_failure = enabled.Any(a => a.StopOnFailure),
                raw_cookies = enabled.Any(a => !string.IsNullOrEmpty(a.RawCookies)),
                jellyseerr_configured = !string.IsNullOrEmpty(cfg?.JellyseerrUrl)
            },
            buckets = new
            {
                accounts = BucketAccounts(enabled.Count),
                library = libraryCount.HasValue ? BucketLibrary(libraryCount.Value) : "unknown",
                syncs_per_week = BucketSyncs(d.WindowSyncs)
            },
            errors = new
            {
                cloudflare_403 = d.WindowErrCloudflare,
                auth_failure = d.WindowErrAuth,
                tmdb_lookup = d.WindowErrTmdb,
                jellyseerr_error = d.WindowErrJellyseerr,
                other = d.WindowErrOther,
                state = new
                {
                    cloudflare_403 = d.StateCloudflare,
                    auth_failure = d.StateAuth,
                    tmdb_lookup = d.StateTmdb,
                    jellyseerr_error = d.StateJellyseerr,
                    other = d.StateOther
                }
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>Set once at startup by TelemetryTask (the only place with IServerApplicationHost access is overkill; the task knows it).</summary>
    internal static string? JellyfinVersion { get; set; }

    internal static string BucketAccounts(int n) => n switch
    {
        <= 0 => "0",
        1 => "1",
        <= 4 => "2-4",
        _ => "5+"
    };

    internal static string BucketLibrary(int n) => n switch
    {
        < 500 => "<500",
        < 2000 => "500-2k",
        < 10000 => "2k-10k",
        _ => "10k+"
    };

    internal static string BucketSyncs(int n) => n switch
    {
        <= 0 => "0",
        <= 10 => "1-10",
        <= 100 => "11-100",
        _ => "100+"
    };

    // ---------- Send / persist ----------

    private static async Task<bool> SendAsync(string json)
    {
        try
        {
            if (SenderOverride != null)
                return await SenderOverride(TelemetryConstants.IngestUrl, json).ConfigureAwait(false);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, TelemetryConstants.IngestUrl);
            req.Headers.TryAddWithoutValidation("x-lbsync-key", TelemetryConstants.IngestKey);
            req.Content = content;
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false; // telemetry sends are best-effort, never retried aggressively
        }
    }

    /// <summary>Test seam: replaces the log-bundle send. Args: (url, jsonBody) -> ref code or null.</summary>
    internal static Func<string, string, Task<string?>>? LogSenderOverride { get; set; }

    /// <summary>
    /// Assembles the EXACT diagnostic bundle JSON that will be uploaded. Both the
    /// preview endpoint and the send endpoint call this, so "preview exactly what's
    /// sent" is literally true: the preview renders this string, the send posts it.
    /// </summary>
    public static string BuildLogBundleJson(
        string instanceId, string pluginVersion, string telemetrySnapshotJson, string? note, List<string> logLines)
    {
        var bundle = new
        {
            instance_id = instanceId,
            plugin_version = pluginVersion,
            jellyfin_version = JellyfinVersion ?? "unknown",
            telemetry = JsonSerializer.Deserialize<JsonElement>(telemetrySnapshotJson),
            note,
            log_lines = logLines
        };
        return JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Uploads a prebuilt bundle JSON (from <see cref="BuildLogBundleJson"/>) to the
    /// backend's /logs endpoint and returns the reference code, or null on failure.
    /// </summary>
    public static async Task<string?> PostLogBundleAsync(string bundleJson)
    {
        try
        {
            var url = TelemetryConstants.IngestUrl.TrimEnd('/') + "/logs";
            if (LogSenderOverride != null)
                return await LogSenderOverride(url, bundleJson).ConfigureAwait(false);

            using var content = new StringContent(bundleJson, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("x-lbsync-key", TelemetryConstants.IngestKey);
            req.Content = content;
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ref_code", out var rc) ? rc.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveIfDueLocked()
    {
        if (UtcNow - _lastSaveUtc >= TimeSpan.FromSeconds(30)) SaveLocked();
    }

    private static void SaveLocked()
    {
        try
        {
            _lastSaveUtc = UtcNow;
            Plugin.Instance?.SaveConfiguration();
        }
        catch { /* persistence is best-effort; counters survive in memory until next save */ }
    }

    /// <summary>Test seam: reset clock/sender overrides and the save debounce.</summary>
    internal static void ResetForTesting()
    {
        SenderOverride = null;
        LogSenderOverride = null;
        ClockOverride = null;
        JellyfinVersion = null;
        _lastSaveUtc = DateTime.MinValue;
    }
}
