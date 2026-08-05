using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Application
{
    internal class SubtitleSyncOrchestrator(
        ISubsyncClient client,
        ISkipCache skipCache,
        ILogger logger,
        IFolderChangeSuppressor suppressor)
    {
        private readonly ISubsyncClient _client = client;
        private readonly ISkipCache _skipCache = skipCache;
        private readonly ILogger _logger = logger;
        private readonly IFolderChangeSuppressor _suppressor = suppressor;

        /// <summary>
        /// Syncs one subtitle from a group: skips it if already synced, picks
        /// what to align it against, then calls the sidecar and updates the
        /// skip-cache on success. Both the subtitle and its reference are
        /// guaranteed to sit in the same directory, so they map to the single
        /// folder the sidecar's /sync accepts. Safe to call concurrently - the
        /// sweep task invokes this from multiple parallel workers at once.
        /// </summary>
        /// <returns>
        /// How the sync ended, or null when nothing was attempted (the file is
        /// gone, already synced, or outside every configured path mapping).
        /// </returns>
        internal async Task<SyncOutcome?> ProcessAsync(
            PluginConfiguration config,
            SubtitleSyncGroup group,
            string subtitlePath,
            CancellationToken cancellationToken)
        {
            // The library row can be stale: the file may have been deleted or
            // replaced since the last scan. IsAlreadySynced hashes the file and
            // throws if it's gone, so this guard is load-bearing.
            if (!File.Exists(subtitlePath) || _skipCache.IsAlreadySynced(subtitlePath))
                return null;

            var referencePath = SubtitleWorkBuilder.ChooseReference(
                subtitlePath,
                group,
                candidate => File.Exists(candidate) && _skipCache.IsAlreadySynced(candidate));

            var subtitleMapping = SubtitleMatcher.ToSidecarAbsolute(subtitlePath, config);
            var referenceFileMapping = SubtitleMatcher.ToSidecarAbsolute(referencePath, config);
            if (subtitleMapping is null || referenceFileMapping is null)
            {
                _logger.LogWarning("Subsync: {Subtitle} is not under any configured WatchedPathsMaps entry, skipping", subtitlePath);
                return null;
            }

            var (folder, subtitleFilename) = subtitleMapping.Value;
            var (_, referenceFilename) = referenceFileMapping.Value;

            _logger.LogInformation("Subsync: syncing {Subtitle} against {Reference}", subtitleFilename, referenceFilename);

            // Jellyfin's watcher must not see the sidecar's write to this
            // folder - otherwise it queues a library refresh whose
            // subtitle-fetch step re-downloads the file we just synced.
            var subtitleDirectory = Path.GetDirectoryName(subtitlePath)!;
            using (_suppressor.Suppress(subtitleDirectory))
            {
                var outcome = await _client
                    .SyncAndWaitAsync(config, folder, referenceFilename, subtitleFilename, cancellationToken)
                    .ConfigureAwait(false);

                // Only a confirmed sync is recorded. A job we timed out on or
                // cancelled may still be finishing on the sidecar, and marking it
                // here would pin a hash for content that hasn't been written yet.
                if (outcome == SyncOutcome.Synced)
                    _skipCache.MarkSynced(subtitlePath);

                return outcome;
            }
        }
    }
}
