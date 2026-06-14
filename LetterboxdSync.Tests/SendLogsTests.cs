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
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for the user-initiated "send logs to developer" bundle. Verifies the
/// bundle shape, that it works with and without telemetry enabled, that the
/// endpoint requires elevation, and that send failure surfaces cleanly.
/// </summary>
[Collection("Plugin")]
public class SendLogsTests : IDisposable
{
    private readonly ControllerTestHarness _h;
    private readonly List<(string Url, string Json)> _sent = new();

    public SendLogsTests()
    {
        _h = new ControllerTestHarness(currentUserId: "11111111111111111111111111111111");
        TelemetryService.ResetForTesting();
        TelemetryService.LogSenderOverride = (url, json) =>
        {
            _sent.Add((url, json));
            return Task.FromResult<string?>("LBX-TEST01");
        };
    }

    public void Dispose()
    {
        TelemetryService.ResetForTesting();
        _h.Dispose();
    }

    private static List<string> Lines() => new() { "[INF] LetterboxdSync started", "[ERR] Letterboxd login error" };

    [Fact]
    public async Task SendLogBundle_PostsToLogsEndpoint_WithExpectedShape()
    {
        var snapshot = TelemetryService.BuildPayload("logs", 1200);
        var built = TelemetryService.BuildLogBundleJson(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "1.17.0.0", snapshot, "diary sync broke", Lines());
        var code = await TelemetryService.PostLogBundleAsync(built);

        Assert.Equal("LBX-TEST01", code);
        var (url, sentJson) = Assert.Single(_sent);
        Assert.EndsWith("/logs", url);
        // Preview integrity: what is built (and previewed) is byte-for-byte what is sent.
        Assert.Equal(built, sentJson);

        using var doc = JsonDocument.Parse(sentJson);
        var root = doc.RootElement;
        Assert.Equal("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", root.GetProperty("instance_id").GetString());
        Assert.Equal("1.17.0.0", root.GetProperty("plugin_version").GetString());
        Assert.Equal("diary sync broke", root.GetProperty("note").GetString());
        Assert.Equal(2, root.GetProperty("log_lines").GetArrayLength());
        // The telemetry snapshot is embedded as structured JSON, not a string.
        Assert.Equal(JsonValueKind.Object, root.GetProperty("telemetry").ValueKind);
        // The preview/bundle MUST contain the actual log text — the consent bug was that
        // the preview showed only the telemetry snapshot, hiding the log lines.
        Assert.Contains("Letterboxd login error", built);
    }

    [Fact]
    public async Task SendLogBundle_NullSender_ReturnsNullOnFailure()
    {
        TelemetryService.LogSenderOverride = (_, _) => Task.FromResult<string?>(null);
        var snapshot = TelemetryService.BuildPayload("logs", null);
        var json = TelemetryService.BuildLogBundleJson(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "1.17.0.0", snapshot, null, Lines());
        var code = await TelemetryService.PostLogBundleAsync(json);
        Assert.Null(code);
    }

    [Theory]
    [InlineData("SendLogs")]
    [InlineData("PreviewLogs")]
    [InlineData("GetLogs")]          // raw server logs name every user's Letterboxd account + films
    [InlineData("GetTelemetryPreview")]
    public void SensitiveEndpoints_RequireElevation(string methodName)
    {
        var method = typeof(LetterboxdController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("RequiresElevation", attr!.Policy);
    }

    // ---- Controller endpoint coverage (the HTTP layer where the consent bug lived) ----

    [Fact]
    public void PreviewLogs_ReturnsFullBundle_IncludingLogLines()
    {
        var logFile = System.IO.Path.Combine(_h.LogDir, "log_20260614.log");
        System.IO.File.WriteAllText(logFile,
            "[2026-06-14 10:00:00.000 +00:00] [INF] [1] LetterboxdSync.Foo: a diagnostic line\n");

        var result = _h.Controller.PreviewLogs();

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        // The preview MUST contain the real log lines AND the telemetry snapshot.
        Assert.Contains("a diagnostic line", content.Content!);
        Assert.Contains("\"telemetry\"", content.Content!);
        Assert.Contains("\"log_lines\"", content.Content!);
    }

    [Fact]
    public async Task SendLogs_Success_ReturnsRefCode()
    {
        var result = await _h.Controller.SendLogs(new SendLogsRequest { Note = "diary broke" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("LBX-TEST01", System.Text.Json.JsonSerializer.Serialize(ok.Value));
        var (_, json) = Assert.Single(_sent);
        Assert.Contains("diary broke", json);
    }

    [Fact]
    public async Task SendLogs_BackendUnreachable_ReturnsBadRequest()
    {
        TelemetryService.LogSenderOverride = (_, _) => Task.FromResult<string?>(null);
        var result = await _h.Controller.SendLogs(new SendLogsRequest { Note = null });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendLogBundle_WorksWithTelemetryDisabled()
    {
        // Telemetry off (default). The bundle should still build and send; the caller
        // supplies a one-off instance id, and the snapshot reflects disabled state.
        Assert.False(_h.Config.Telemetry.Enabled);
        var snapshot = TelemetryService.BuildPayload("logs", 100);
        var json = TelemetryService.BuildLogBundleJson(Guid.NewGuid().ToString(), "1.17.0.0", snapshot, null, Lines());
        var code = await TelemetryService.PostLogBundleAsync(json);
        Assert.Equal("LBX-TEST01", code);
        Assert.Single(_sent);
    }
}
