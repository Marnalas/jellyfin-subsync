using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Application
{
    internal class SubtitleSyncOrchestrator(SubsyncClient client, SkipCache skipCache, ILogger logger)
    {
        private readonly SubsyncClient _client = client;
        private readonly SkipCache _skipCache = skipCache;
        private readonly ILogger _logger = logger;

        /// <summary>
        /// Syncs one subtitle from a group: skips it if already synced, picks
        /// what to align it against, then calls the sidecar and updates the
        /// skip-cache on success. Both the subtitle and its reference are
        /// guaranteed to sit in the same directory, so they map to the single
        /// folder the sidecar's /sync accepts. Safe to call concurrently - the
        /// sweep task invokes this from multiple parallel workers at once.
        /// </summary>
        internal async Task ProcessAsync(SubtitleSyncGroup group, string subtitlePath, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;

            // The library row can be stale: the file may have been deleted or
            // replaced since the last scan. IsAlreadySynced hashes the file and
            // throws if it's gone, so this guard is load-bearing.
            if (!File.Exists(subtitlePath) || _skipCache.IsAlreadySynced(subtitlePath))
                return;

            var referencePath = SubtitleWorkBuilder.ChooseReference(
                subtitlePath,
                group,
                candidate => File.Exists(candidate) && _skipCache.IsAlreadySynced(candidate));

            var subtitleMapping = SubtitleMatcher.ToSidecarAbsolute(subtitlePath, config);
            var referenceFileMapping = SubtitleMatcher.ToSidecarAbsolute(referencePath, config);
            if (subtitleMapping is null || referenceFileMapping is null)
            {
                _logger.LogWarning("Subsync: {Subtitle} is not under any configured WatchedPathsMaps entry, skipping", subtitlePath);
                return;
            }

            var (folder, subtitleFilename) = subtitleMapping.Value;
            var (_, referenceFilename) = referenceFileMapping.Value;

            _logger.LogInformation("Subsync: syncing {Subtitle} against {Reference}", subtitleFilename, referenceFilename);

            var ok = await _client.SyncAndWaitAsync(folder, referenceFilename, subtitleFilename, cancellationToken)
                .ConfigureAwait(false);

            if (ok)
                _skipCache.MarkSynced(subtitlePath);
        }
    }
}
