using Jellyfin.Data.Enums;
using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Asks Jellyfin what it already knows. Every video item in the library
/// carries a MediaStream list in which Jellyfin's own naming layer has
/// already identified the external subtitle files, resolved their language
/// and flags, and - crucially - decided which video each one belongs to.
/// This class does nothing but walk that, so the plugin never has to guess
/// the association from a filename again.
/// </summary>
internal sealed class LibrarySubtitleSource(
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    ILogger logger)
{
    /// <summary>
    /// Yields one group per library item that has subtitles worth syncing.
    /// Ids are fetched up front (a Guid list costs a couple of MB even on a
    /// six-figure library) but items are hydrated one at a time, so syncing
    /// starts immediately instead of after a full-library materialisation.
    /// That id list doubles as the progress denominator: every item is
    /// credited to <paramref name="progress"/> here if it's skipped, or by
    /// the caller once its group has been processed.
    /// </summary>
    internal IEnumerable<SubtitleSyncGroup> EnumerateGroups(
        PluginConfiguration config,
        SweepProgress progress,
        CancellationToken cancellationToken)
    {
        var itemIds = libraryManager.GetItemIds(BuildVideoItemsQuery());
        logger.LogInformation("Subsync sweep: inspecting {Count} library video item(s)", itemIds.Count);
        progress.SetTotal(itemIds.Count);

        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = libraryManager.GetItemById(id);
            if (item is null)
            {
                // Deleted between the id query and here.
                progress.ItemDone();
                continue;
            }

            IReadOnlyList<MediaStream> subtitleStreams;
            try
            {
                subtitleStreams = mediaSourceManager.GetMediaStreams(new MediaStreamQuery
                {
                    ItemId = item.Id,
                    Type = MediaStreamType.Subtitle
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Subsync sweep: failed to read media streams for {Item}, skipping",
                    item.Path ?? item.Id.ToString());
                progress.ItemDone();
                continue;
            }

            if (subtitleStreams.Count == 0)
            {
                // By far the common case on a typical library.
                progress.ItemDone();
                continue;
            }

            // ISO / BDMV / VIDEO_TS: no single elementary video file for
            // ffsubsync to align against. Read from metadata, not a stat.
            var isDiscImageOrFolder = item is Video video && video.VideoType != VideoType.VideoFile;

            var work = SubtitleWorkBuilder.BuildWork(item.Path, isDiscImageOrFolder, subtitleStreams, config);
            foreach (var subtitle in work.SubtitlesInOtherDirectories)
            {
                logger.LogWarning(
                    "Subsync sweep: {Subtitle} is not in the same folder as {Video}; the sidecar syncs one folder at a time, skipping",
                    subtitle,
                    item.Path);
            }

            switch (work.Reason)
            {
                case ItemSkipReason.PathIsDiscImageOrFolder:
                    logger.LogWarning(
                        "Subsync sweep: {Path} is a disc image or disc folder with no single video file to align against, skipping its subtitles",
                        item.Path);
                    break;
                case ItemSkipReason.NoPath:
                    logger.LogDebug("Subsync sweep: item {Id} has no file path, skipping", item.Id);
                    break;
            }

            if (work.Group is not null)
                yield return work.Group;
            else
                // Nothing to hand to the caller, so nobody else will credit
                // this item towards the progress bar.
                progress.ItemDone();
        }
    }

    private static InternalItemsQuery BuildVideoItemsQuery() => new()
    {
        // Every item Jellyfin considers a video, in one predicate: Movie,
        // Episode, Video, MusicVideo, Trailer and extras all qualify. An
        // IncludeItemTypes list would have to enumerate them and would
        // silently miss whichever kind we forgot.
        MediaTypes = [MediaType.Video],

        // Missing-episode placeholders and other virtual rows have no file
        // on disk; each would cost a wasted pair of lookups below.
        IsVirtualItem = false,

        // Cheap guard against folder-typed video containers. NOT sufficient
        // on its own for BDMV/VIDEO_TS - see the VideoType check.
        IsFolder = false,

        // Without this, LibraryManager can restrict the query to top-level
        // items only.
        Recursive = true
    };
}

