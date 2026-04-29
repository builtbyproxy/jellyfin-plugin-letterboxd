using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync;

public class SyncTask : IScheduledTask
{
    private readonly LetterboxdSyncRunner _runner;

    public SyncTask(LetterboxdSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync watched movies to Letterboxd";
    public string Key => "LetterboxdSync";
    public string Description => "Syncs your Jellyfin watch history to your Letterboxd diary";
    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunForAllAsync(progress, "scheduled", cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = System.TimeSpan.FromDays(1).Ticks
        }
    };
}
