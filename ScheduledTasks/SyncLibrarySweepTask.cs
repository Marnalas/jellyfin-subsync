using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.ScheduledTasks
{
    /// <summary>
    /// Walks every configured library path and syncs any subtitle the
    /// skip-cache doesn't already know about. The skip-cache makes repeat
    /// sweeps a cheap no-op for anything already handled, so this is the
    /// sole catch-all mechanism for anything added since the last sweep -
    /// there is no filesystem watcher or instant trigger (see
    /// ARCHITECTURE.md). Mirrors the scheduled-sweep half of the
    /// "scheduled sweep + skip cache" pattern used by whisper-subs, run on
    /// startup and on an interval by default.
    /// </summary>
    public class SyncLibrarySweepTask(
        ILogger<SyncLibrarySweepTask> logger,
        SubsyncClient client,
        SkipCache skipCache) : IScheduledTask
    {
        private readonly ILogger<SyncLibrarySweepTask> _logger = logger;
        private readonly SubtitleSyncOrchestrator _orchestrator = new(client, skipCache, logger);

        public string Name => "Sync unsynced subtitles";

        public string Key => "SubsyncLibrarySweep";

        public string Description => "Scans your libraries for subtitles that haven't been synced yet and syncs them.";

        public string Category => "Subsync Starter";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var paths = config.WatchedPathsMaps.Select(entry => entry.JellyfinPath).ToList();
            var maxParallelJobs = Math.Max(1, config.MaxParallelJobs);
            var processed = 0;

            // Up to maxParallelJobs file groups are processed in parallel, globally
            // across every watched path; each still fully round-trips (submit + poll
            // to completion) within its own slot, so extra parallelism here only pays
            // off if the sidecar's MAX_PARALLEL_JOBS is raised to match. Subtitles
            // belonging to the same video file are synced one at a time within a group
            // (instead of also being spread across slots) so that after the first one
            // syncs against the video file, the rest see it as an already-synced sibling and
            // sync against that instead - much faster than each of them re-syncing
            // against the video file independently. Groups are streamed lazily directory by
            // directory rather than collected upfront, so a huge library never sits
            // fully buffered in memory before syncing starts.
            await Parallel.ForEachAsync(
                SubtitleMatcher.EnumerateSubtitleGroups(paths, config, _logger),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelJobs, CancellationToken = cancellationToken },
                async (group, ct) =>
                {
                    foreach (var subtitlePath in group)
                    {
                        await _orchestrator.ProcessAsync(subtitlePath, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref processed);
                    }
                }).ConfigureAwait(false);

            _logger.LogInformation("Subsync sweep: checked all watched paths, {Count} subtitle(s) touched", processed);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Default daily run at 02:00. Shows up in the admin UI as an
            // editable time-of-day trigger, same as core tasks like
            // "Scan Media Library".
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
                MaxRuntimeTicks = TimeSpan.FromHours(2).Ticks
            };
        }
    }
}
