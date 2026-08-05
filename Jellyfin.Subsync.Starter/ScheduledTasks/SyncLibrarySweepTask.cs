using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.ScheduledTasks
{
    /// <summary>
    /// Walks the video items in the Jellyfin library and syncs any external
    /// subtitle the skip-cache doesn't already know about. Jellyfin has already
    /// worked out which subtitle file belongs to which video, so this never
    /// re-derives that from filenames; the tradeoff is that a subtitle Jellyfin
    /// hasn't indexed yet is invisible until the next library scan. The
    /// skip-cache makes repeat sweeps a cheap no-op for anything already
    /// handled, so this is the sole catch-all mechanism - there is no
    /// filesystem watcher or instant trigger.
    /// </summary>
    public class SyncLibrarySweepTask(
        ILogger<SyncLibrarySweepTask> logger,
        ISubsyncClient client,
        ISkipCache skipCache,
        IPluginConfigurationProvider configurationProvider,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IFolderChangeSuppressor suppressor) : IScheduledTask
    {
        /// <summary>
        /// A "Run Now" fired seconds after `docker compose up` shouldn't abort
        /// on a sidecar that's still importing ffsubsync, so the health check
        /// gets a few tries before the sweep gives up on it.
        /// </summary>
        private const int HealthCheckAttempts = 3;
        private static readonly TimeSpan HealthCheckRetryDelay = TimeSpan.FromSeconds(5);

        private readonly ILogger<SyncLibrarySweepTask> _logger = logger;
        private readonly ISubsyncClient _client = client;
        private readonly ISkipCache _skipCache = skipCache;
        private readonly IPluginConfigurationProvider _configurationProvider = configurationProvider;
        private readonly SubtitleSyncOrchestrator _orchestrator = new(client, skipCache, logger, suppressor);
        private readonly LibrarySubtitleSource _source = new(libraryManager, mediaSourceManager, logger);

        public string Name => "Sync unsynced subtitles";

        public string Key => "SubsyncLibrarySweep";

        public string Description => "Syncs the external subtitles in your libraries that haven't been synced yet. Only subtitles Jellyfin has already indexed are considered, so run a library scan first if you've just added some.";

        public string Category => "Subsync Starter";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            // Read once, then threaded down as a parameter. A sweep can run for
            // hours; re-reading it per file meant a config save halfway through
            // silently changed the path mappings and timeouts underneath a run
            // already in progress.
            var config = _configurationProvider.GetSnapshot();
            var maxParallelJobs = Math.Max(1, config.MaxParallelJobs);
            var processed = 0;
            var sweepProgress = new SweepProgress(progress);

            // Ask the sidecar before walking a six-figure library: without this,
            // an unreachable sidecar produced one timeout per subtitle for the
            // length of the whole run, with the actual cause buried in the noise.
            if (!await IsSidecarReachableAsync(config, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogError(
                    "Subsync sweep: the sidecar at {Url} did not answer /health after {Attempts} attempts, so "
                    + "nothing could sync a subtitle - aborting the sweep. Check that the sidecar container is "
                    + "running and that Sidecar URL matches its compose service name and port",
                    config.SidecarUrl, HealthCheckAttempts);

                // Thrown rather than returned quietly so Dashboard > Scheduled
                // Tasks shows this as Failed. A sweep that never ran reporting a
                // green "Completed" is how a broken setup goes unnoticed for
                // weeks.
                throw new InvalidOperationException(
                    $"The subsync sidecar at {config.SidecarUrl} is not reachable, so the sweep was aborted.");
            }

            try
            {
                // Up to maxParallelJobs items are processed in parallel; each still
                // fully round-trips (submit + poll to completion) within its own slot,
                // so extra parallelism here only pays off if the sidecar's
                // MAX_PARALLEL_JOBS is raised to match. The subtitles of one video are
                // synced one at a time within a group (instead of also being spread
                // across slots) so that after the first one syncs against the video
                // file, the rest see it as an already-synced sibling and sync against
                // that instead - much faster than each of them re-syncing against the
                // video file independently. Groups are streamed lazily item by item
                // rather than collected upfront, so a huge library never sits fully
                // buffered in memory before syncing starts - only the id list is
                // materialised, which is also what gives the progress bar its
                // denominator. ForEachAsync pulls the next group only as a slot
                // frees up rather than draining the enumerable, so how far the
                // enumeration has got is a fair stand-in for how far the sweep has.
                await Parallel.ForEachAsync(
                    _source.EnumerateGroups(config, sweepProgress, cancellationToken),
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelJobs, CancellationToken = cancellationToken },
                    async (group, ct) =>
                    {
                        foreach (var subtitlePath in group.SubtitlePaths)
                        {
                            // A throw here would otherwise cancel every other worker
                            // and abandon the rest of the sweep, so a single bad file
                            // (unreadable subtitle, transient IO error) can't cost a
                            // multi-hour run. Real cancellation is still honoured.
                            try
                            {
                                await _orchestrator.ProcessAsync(config, group, subtitlePath, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Subsync sweep: failed to process {Subtitle}, continuing", subtitlePath);
                            }

                            Interlocked.Increment(ref processed);
                        }

                        // Deliberately not in a finally: a rethrown cancellation
                        // means this item's remaining subtitles were abandoned, and
                        // crediting it would overstate how far the sweep got.
                        sweepProgress.ItemDone();
                    }).ConfigureAwait(false);
            }
            finally
            {
                // Saves are batched, so the last few marks are still in memory
                // here. This is the only guaranteed point to get them on disk -
                // including on a cancelled run, where everything already synced
                // still deserves to be skipped next time.
                _skipCache.Flush();
            }

            // After the sweep, never before it: by now the mounts have
            // demonstrably been live, whereas at sweep start a volume that
            // hasn't finished mounting looks exactly like a deleted library.
            // Outside the finally for the same reason - a cancelled sweep never
            // visited most of the library and has no business pruning it.
            var removed = _skipCache.RemoveMissingFiles();
            if (removed > 0)
            {
                _logger.LogInformation(
                    "Subsync sweep: dropped {Count} skip-cache entr(ies) for files that no longer exist", removed);
                _skipCache.Flush();
            }

            _logger.LogInformation("Subsync sweep: checked the library, {Count} subtitle(s) touched", processed);

            // An empty library never reports anything above (there's no
            // denominator to divide by), and rounding down can leave the bar a
            // fraction short on the last item.
            progress.Report(100);
        }

        private async Task<bool> IsSidecarReachableAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= HealthCheckAttempts; attempt++)
            {
                if (await _client.IsHealthyAsync(config, cancellationToken).ConfigureAwait(false))
                    return true;

                if (attempt < HealthCheckAttempts)
                {
                    _logger.LogWarning(
                        "Subsync sweep: the sidecar didn't answer /health (attempt {Attempt} of {Attempts}), retrying",
                        attempt, HealthCheckAttempts);
                    await Task.Delay(HealthCheckRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
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
