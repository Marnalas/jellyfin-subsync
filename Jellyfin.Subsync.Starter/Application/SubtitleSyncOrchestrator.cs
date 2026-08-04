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
        /// Processes a single subtitle path: matches it to a video, skips it
        /// if already synced, otherwise calls the sidecar and updates the
        /// skip-cache on success. Safe to call concurrently - the sweep task
        /// invokes this from multiple parallel workers at once.
        /// </summary>
        internal async Task ProcessAsync(string subtitlePath, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;

            if (!SubtitleMatcher.IsSubtitleFile(subtitlePath, config)
                || !File.Exists(subtitlePath)
                || _skipCache.IsAlreadySynced(subtitlePath))
            {
                return;
            }

            var relatedFiles = SubtitleMatcher.FindRelatedFiles(subtitlePath, config);
            if (relatedFiles is null)
            {
                _logger.LogDebug("Subsync: no related files for {Subtitle}", subtitlePath);
                return;
            }

            var referenceFile = relatedFiles
                .FirstOrDefault(relatedFile => relatedFile.Type == FileType.Subtitle && _skipCache.IsAlreadySynced(relatedFile.FilePath))
                ?? relatedFiles.FirstOrDefault(rf => rf.Type == FileType.Movie);
            if (referenceFile is null)
            {
                _logger.LogDebug("Subsync: no already synced subtitle or movie for {Subtitle}", subtitlePath);
                return;
            }

            var subtitleMapping = SubtitleMatcher.ToSidecarAbsolute(subtitlePath, config);
            var referenceFileMapping = SubtitleMatcher.ToSidecarAbsolute(referenceFile.FilePath, config);
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
            {
                _skipCache.MarkSynced(subtitlePath);
            }
        }
    }
}
