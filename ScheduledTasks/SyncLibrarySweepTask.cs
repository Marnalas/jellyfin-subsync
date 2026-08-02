using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.ScheduledTasks
{
    /// <summary>
    /// Walks every configured library path and syncs any subtitle the
    /// skip-cache doesn't already know about. Runs on the same skip-cache
    /// as the instant watcher, so this is a cheap no-op sweep for anything
    /// already handled, and a genuine catch-all for anything that arrived
    /// while the watcher was down, was added outside Jellyfin, etc.
    /// Mirrors the "scheduled sweep + skip cache" pattern used by
    /// whisper-subs, run on startup and on an interval by default.
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

        public string Description => "Scans your libraries for subtitles that haven't been GPU-synced yet and syncs them.";

        public string Category => "Subsync Starter";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var paths = config.WatchedPathsMaps.Select(entry => entry.JellyfinPath).ToList();
            var maxParallelJobs = Math.Max(1, config.MaxParallelJobs);
            var processed = 0;

            for (var i = 0; i < paths.Count; i++)
            {
                var root = paths[i];
                if (!Directory.Exists(root))
                {
                    _logger.LogWarning("Subsync sweep: path does not exist, skipping: {Path}", root);
                    continue;
                }

                IEnumerable<string> subtitles;
                try
                {
                    subtitles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(path => SubtitleMatcher.IsSubtitleFile(path, config));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync sweep: failed to enumerate {Path}", root);
                    continue;
                }

                // Up to maxParallelJobs subtitles are submitted to the sidecar at once;
                // each still fully round-trips (submit + poll to completion) within its
                // own slot, so extra parallelism here only pays off if the sidecar's
                // MAX_PARALLEL_JOBS is raised to match.
                await Parallel.ForEachAsync(
                    subtitles,
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelJobs, CancellationToken = cancellationToken },
                    async (subtitlePath, ct) =>
                    {
                        await _orchestrator.ProcessAsync(subtitlePath, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref processed);
                    }).ConfigureAwait(false);

                progress.Report((i + 1) * 100.0 / paths.Count);
            }

            _logger.LogInformation("Subsync sweep: checked all watched paths, {Count} subtitle(s) touched", processed);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // These are only the DEFAULTS, used the first time the task is
            // ever registered. Once installed, the admin can add/remove/
            // edit triggers - including this daily time - from Dashboard >
            // Scheduled Tasks > "Sync unsynced subtitles" > Edit. Jellyfin
            // core persists those edits itself; nothing to do on the
            // plugin side beyond declaring a sensible starting point.

            // Run once on every server startup, so anything missed while
            // Jellyfin was down gets caught immediately rather than
            // waiting for the next scheduled time.
            yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };

            // Default daily run at 02:00. Shows up in the admin UI as an
            // editable time-of-day trigger, same as core tasks like
            // "Scan Media Library".
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
            };
        }
    }
}
