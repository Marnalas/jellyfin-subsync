using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Application;

internal class SubtitleSyncOrchestrator(
    ISubsyncClient client,
    ISkipCache skipCache,
    IFailCache failCache,
    ILogger logger,
    IFolderChangeSuppressor suppressor)
{
    /// <summary>
    /// Syncs one subtitle from a group: skips it if already synced, picks
    /// what to align it against, then calls the sidecar and updates the
    /// skip-cache on success. Both the subtitle and its reference are
    /// guaranteed to sit in the same directory, so they map to the single
    /// folder the sidecar's /sync accepts. Safe to call concurrently - the
    /// sweep task invokes this from multiple parallel workers at once.
    /// </summary>
    /// <param name="config">Sidecar URL, timeouts and path mappings for this run.</param>
    /// <param name="group">The subtitle's video and its sibling subtitles - see <see cref="SubtitleSyncGroup"/>.</param>
    /// <param name="subtitlePath">The subtitle file to align and overwrite.</param>
    /// <param name="cancellationToken">Cancels the submit-and-poll round trip.</param>
    /// <param name="referencePathOverride">
    /// When given, syncs <paramref name="subtitlePath"/> against exactly
    /// this file instead of letting <see cref="SubtitleWorkBuilder.ChooseReference"/>
    /// pick one automatically - for a user-directed "sync this one subtitle
    /// against that one" request, where the caller (not the skip-cache)
    /// knows best what to align against. Bypasses the "is it already
    /// synced" requirement <see cref="SubtitleWorkBuilder.ChooseReference"/>
    /// applies to a sibling candidate, so it still needs its own existence
    /// check below.
    /// </param>
    /// <returns>
    /// How the sync ended, or null when nothing was attempted (the file is
    /// gone, already synced, or outside every configured path mapping).
    /// </returns>
    internal async Task<SyncOutcome?> ProcessAsync(
        PluginConfiguration config,
        SubtitleSyncGroup group,
        string subtitlePath,
        CancellationToken cancellationToken,
        string? referencePathOverride = null)
    {
        // The library row can be stale: the file may have been deleted or
        // replaced since the last scan. IsCached hashes the file and
        // throws if it's gone, so this guard is load-bearing.
        if (!File.Exists(subtitlePath) || skipCache.IsCached(subtitlePath))
            return null;

        if (failCache.IsCached(subtitlePath))
        {
            logger.LogDebug("Subsync: skipping {Subtitle} - failed too many times in a row", subtitlePath);
            return null;
        }

        // The auto-picked path is only ever accepted once ChooseReference's
        // own isAlreadySynced predicate has confirmed it exists; an override
        // skips that predicate entirely; so unlike the auto-picked case,
        // this needs its own existence check - otherwise a stale index (the
        // file was deleted/renamed since whoever built the picker read it)
        // would reach the sidecar as a reference that isn't there.
        if (referencePathOverride is not null && !File.Exists(referencePathOverride))
        {
            logger.LogWarning(
                "Subsync: reference {Reference} no longer exists, skipping {Subtitle}",
                referencePathOverride, subtitlePath);
            return null;
        }

        var referencePath = referencePathOverride ?? SubtitleWorkBuilder.ChooseReference(
            subtitlePath,
            group,
            candidate => File.Exists(candidate) && skipCache.IsCached(candidate));

        // Only meaningful when the sidecar would be deciding on its own what
        // to align against - i.e. the reference is the video, not an
        // already-synced sibling subtitle, where a sidecar-side alignment
        // choice has no effect at all. Whatever this fact implies for the
        // sidecar's own alignment strategy is entirely its call; the plugin
        // only reports what Jellyfin told it.
        var embeddedSubtitleSituation = referencePath == group.VideoPath
            ? group.EmbeddedSubtitleSituation
            : EmbeddedSubtitleSituation.Irrelevant;
        var embeddedSubtitleIndex = referencePath == group.VideoPath
            ? group.EmbeddedSubtitleIndex
            : null;

        var subtitleMapping = SubtitleMatcher.ToSidecarAbsolute(subtitlePath, config);
        var referenceFileMapping = SubtitleMatcher.ToSidecarAbsolute(referencePath, config);
        if (subtitleMapping is null || referenceFileMapping is null)
        {
            logger.LogWarning("Subsync: {Subtitle} is not under any configured WatchedPathsMaps entry, skipping",
                subtitlePath);
            return null;
        }

        var (folder, subtitleFilename) = subtitleMapping.Value;
        var (_, referenceFilename) = referenceFileMapping.Value;

        logger.LogInformation("Subsync: syncing {Subtitle} against {Reference}", subtitleFilename, referenceFilename);

        // Jellyfin's watcher must not see the sidecar's write to this
        // folder - otherwise it queues a library refresh whose
        // subtitle-fetch step re-downloads the file we just synced.
        var subtitleDirectory = Path.GetDirectoryName(subtitlePath)!;
        using (suppressor.Suppress(subtitleDirectory))
        {
            var outcome = await client
                .SyncAndWaitAsync(config, folder, referenceFilename, subtitleFilename, cancellationToken,
                    embeddedSubtitleSituation, embeddedSubtitleIndex)
                .ConfigureAwait(false);

            // Only a confirmed sync is recorded. A job we timed out on or
            // canceled may still be finishing on the sidecar, and marking it
            // here would pin a hash for content that hasn't been written yet.
            // Likewise, only a definite "this file can't be synced" counts
            // against its failure streak - not a transport/timeout outcome,
            // which says nothing about the file itself.
            switch (outcome)
            {
                case SyncOutcome.Synced:
                    skipCache.AddToCache(subtitlePath);
                    failCache.RemoveForPath(subtitlePath);
                    break;
                case SyncOutcome.Failed:
                    failCache.AddToCache(subtitlePath);
                    break;
            }

            return outcome;
        }
    }
}