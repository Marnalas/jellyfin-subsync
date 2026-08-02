using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Configuration;
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

        public string Description => "Scans your libraries for subtitles that haven't been GPU-synced yet and syncs them.";

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
                EnumerateSubtitleGroups(paths, config, progress),
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

        /// <summary>
        /// Walks every watched path directory by directory and, within each
        /// directory, groups its subtitle files by the video file they belong to
        /// (same base name, e.g. "Movie.eng.srt" and "Movie.rus.srt" both
        /// belong to "Movie"). Yields one group at a time so a directory's
        /// handful of subtitles is buffered, never the whole library.
        /// </summary>
        private IEnumerable<IReadOnlyList<string>> EnumerateSubtitleGroups(List<string> paths, PluginConfiguration config, IProgress<double> progress)
        {
            for (var i = 0; i < paths.Count; ++i)
            {
                var root = paths[i];
                if (!Directory.Exists(root))
                {
                    _logger.LogWarning("Subsync sweep: path does not exist, skipping: {Path}", root);
                    // progress.Report((i + 1) * 100.0 / paths.Count); // doesn't really indicate progress as-is
                    continue;
                }

                using var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Prepend(root)
                    .GetEnumerator();

                while (true)
                {
                    string directory;
                    try
                    {
                        if (!directories.MoveNext())
                        {
                            break;
                        }

                        directory = directories.Current;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Subsync sweep: failed to enumerate {Path}", root);
                        break;
                    }

                    List<string> subtitlesInDirectory;
                    try
                    {
                        subtitlesInDirectory = [..
                            Directory.EnumerateFiles(directory,"*", SearchOption.TopDirectoryOnly)
                                .Where(path => SubtitleMatcher.IsSubtitleFile(path, config))];
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Subsync sweep: failed to list {Path}", directory);
                        continue;
                    }

                    foreach (var group in
                        subtitlesInDirectory
                            .GroupBy(path => SubtitleMatcher.GetBaseName(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase))
                    {
                        yield return group.ToList();
                    }
                }

                // progress.Report((i + 1) * 100.0 / paths.Count); // doesn't really indicate progress as-is
            }
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
