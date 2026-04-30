using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync;

public class WatchlistSyncTask : IScheduledTask
{
    private readonly WatchlistSyncRunner _runner;

    public WatchlistSyncTask(WatchlistSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync Letterboxd watchlist to playlist";
    public string Key => "LetterboxdWatchlistSync";
    public string Description => "Creates a Jellyfin playlist from your Letterboxd watchlist";
    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunForAllAsync(progress, "scheduled", cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        }
    };
}
