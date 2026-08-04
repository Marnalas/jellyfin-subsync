using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    /// <summary>
    /// Turns one library item and the subtitle MediaStreams Jellyfin resolved
    /// for it into the sync work the sweep should do. Deliberately pure: no
    /// filesystem, no Jellyfin services, no Plugin.Instance - which is what
    /// makes it unit-testable without a mocking library, and keeps
    /// LibrarySubtitleSource down to "call two APIs and log".
    /// </summary>
    internal static class SubtitleWorkBuilder
    {
        /// <summary>
        /// Builds the ordered list of subtitle files to sync for one item, in
        /// the order they should be synced. Association is entirely Jellyfin's:
        /// a stream is this item's subtitle because Jellyfin's naming layer said
        /// so, not because a filename matched a pattern here.
        /// </summary>
        /// <param name="itemPath">The item's video file path (BaseItem.Path).</param>
        /// <param name="isDiscImageOrFolder">True for ISO/BDMV/VIDEO_TS items, which have no single video file to align against.</param>
        /// <param name="subtitleStreams">The item's subtitle MediaStreams, external and embedded alike.</param>
        /// <param name="config">Supplies SubtitleExtensions.</param>
        internal static ItemSubtitleWork BuildWork(
            string? itemPath,
            bool isDiscImageOrFolder,
            IReadOnlyList<MediaStream> subtitleStreams,
            PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(itemPath))
                return new ItemSubtitleWork(null, ItemSkipReason.NoPath, []);
            if (isDiscImageOrFolder)
                return new ItemSubtitleWork(null, ItemSkipReason.PathIsDiscImageOrFolder, []);
            var videoDirectory = Path.GetDirectoryName(itemPath);
            if (string.IsNullOrEmpty(videoDirectory))
                return new ItemSubtitleWork(null, ItemSkipReason.NoPath, []);

            List<string> beside = [];
            List<string> elsewhere = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var forced = new HashSet<string>(StringComparer.Ordinal);

            // Ordering is by stream index then path so a run is reproducible:
            // whichever subtitle comes first syncs against the video, and the
            // rest then find it as an already-synced sibling.
            var candidates = subtitleStreams
                .Where(stream => stream.Type == MediaStreamType.Subtitle
                    && stream.IsExternal
                    && stream.IsExternalUrl != true
                    && !string.IsNullOrEmpty(stream.Path))
                .OrderBy(stream => stream.Index)
                .ThenBy(stream => stream.Path, StringComparer.Ordinal);

            foreach (var stream in candidates)
            {
                var path = stream.Path;

                // Jellyfin can report the same external file more than once when
                // an item has several media sources or alternate versions.
                if (!seen.Add(path))
                    continue;

                // Still the plugin's own gate, for two reasons. Jellyfin indexes
                // "Movie.en_original_backup.srt" as an external subtitle of
                // "Movie.mkv" ("Movie" + "." + flags it can't parse), so the
                // sidecar's own byproducts would otherwise be fed straight back
                // in as work. And Jellyfin's subtitle extension set is wider
                // than the configured SubtitleExtensions.
                if (!SubtitleMatcher.IsSubtitleFile(path, config))
                    continue;

                // SubsyncClient.SyncAndWaitAsync takes ONE folder plus two
                // filenames, so a subtitle that doesn't sit next to its video
                // can't be expressed. In practice this only catches subtitles
                // under Jellyfin's own internal metadata path.
                if (!string.Equals(Path.GetDirectoryName(path), videoDirectory, StringComparison.Ordinal))
                {
                    elsewhere.Add(path);
                    continue;
                }

                beside.Add(path);
                if (stream.IsForced)
                    forced.Add(path);
            }

            return beside.Count == 0
                ? new ItemSubtitleWork(null, ItemSkipReason.NoUsableSubtitles, elsewhere)
                : new ItemSubtitleWork(new SubtitleSyncGroup(itemPath, beside, forced), ItemSkipReason.None, elsewhere);
        }

        /// <summary>
        /// Picks what a subtitle should be aligned against: an non-forced
        /// already-synced sibling if there is one - aligning subtitle-to-subtitle
        /// needs no audio extraction and is much faster - otherwise the video itself.
        /// The "is it already synced" test is injected so the skip-cache and its
        /// file IO stay out of here.
        /// </summary>
        internal static string ChooseReference(
            string subtitlePath,
            SubtitleSyncGroup group,
            Func<string, bool> isAlreadySynced)
        {
            for (var i = 0; i < group.SubtitlePaths.Count; ++i)
            {
                var candidate = group.SubtitlePaths[i];
                if (string.Equals(candidate, subtitlePath, StringComparison.Ordinal)
                    || group.ForcedSubtitlePaths?.Contains(candidate) == true)
                    continue;
                if (isAlreadySynced(candidate))
                    return candidate;
            }

            return group.VideoPath;
        }
    }
}
