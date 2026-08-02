using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Application
{
    public class SubtitleSyncOrchestrator(SubsyncClient client, SkipCache skipCache, ILogger logger)
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
        public async Task ProcessAsync(string subtitlePath, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;

            if (!SubtitleMatcher.IsSubtitleFile(subtitlePath, config) || !File.Exists(subtitlePath))
            {
                return;
            }

            if (_skipCache.IsAlreadySynced(subtitlePath))
            {
                return;
            }

            var moviePath = SubtitleMatcher.FindMovieFile(subtitlePath, config);
            if (moviePath is null)
            {
                _logger.LogDebug("Subsync: no matching video for {Subtitle}", subtitlePath);
                return;
            }

            var subtitleMapping = SubtitleMatcher.ToSidecarAbsolute(subtitlePath, config);
            var movieMapping = SubtitleMatcher.ToSidecarAbsolute(moviePath, config);
            if (subtitleMapping is null || movieMapping is null)
            {
                _logger.LogWarning("Subsync: {Subtitle} is not under any configured WatchedPathsMaps entry, skipping", subtitlePath);
                return;
            }

            var (folder, subtitleFilename) = subtitleMapping.Value;
            var (_, movieFilename) = movieMapping.Value;

            _logger.LogInformation("Subsync: syncing {Subtitle} against {Movie}", subtitleFilename, movieFilename);

            var ok = await _client.SyncAndWaitAsync(folder, movieFilename, subtitleFilename, cancellationToken)
                .ConfigureAwait(false);

            if (ok)
            {
                _skipCache.MarkSynced(subtitlePath);
            }
        }
    }
}
