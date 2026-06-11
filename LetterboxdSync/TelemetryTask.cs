using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Daily heartbeat for telemetry. Runs every day but TelemetryService only sends the
/// weekly ping once the UTC week has rolled over since the last successful ping (and the
/// per-instance jitter minute has passed); the daily cadence also drains any queued
/// error-transition ping once its daily cap reopens. A no-op while telemetry is disabled.
/// </summary>
public class TelemetryTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<TelemetryTask> _logger;

    public TelemetryTask(ILibraryManager libraryManager, IServerApplicationHost appHost, ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _appHost = appHost;
        _logger = loggerFactory.CreateLogger<TelemetryTask>();
    }

    public string Name => "Send anonymous usage telemetry";
    public string Key => "LetterboxdSyncTelemetry";
    public string Description => "Sends an anonymous weekly usage ping if telemetry is enabled in Letterboxd Sync settings. Does nothing while telemetry is off (the default).";
    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        TelemetryService.JellyfinVersion ??= _appHost.ApplicationVersionString;
        return TelemetryService.RunScheduledAsync(CountMovies(), _logger);
    }

    private int? CountMovies()
    {
        try
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            }).Count;
        }
        catch
        {
            return null; // payload reports "unknown" rather than failing the ping
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        }
    };
}
