using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Subsync.Starter.Infrastructure;

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
    /// Jellyfin's normalized names (<c>ProbeResultNormalizer.NormalizeSubtitleCodec</c>)
    /// for image-based subtitle codecs, not ffprobe's raw codec_name - these
    /// never enter ffsubsync's own text-subtitle comparison (its
    /// _BITMAP_SUBTITLE_CODECS), so a full one must never count as "there's
    /// a good text track" here. Compared case-insensitively throughout;
    /// XSUB isn't in Jellyfin's normalization list, so its casing as
    /// reported isn't guaranteed.
    /// </summary>
    private static readonly string[] BitmapSubtitleCodecs = ["PGSSUB", "DVBSUB", "DVBTXT", "DVDSUB", "XSUB"];

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
            .Where(stream => stream is { Type: MediaStreamType.Subtitle, IsExternal: true }
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

        // Not used to pick sync candidates - only external streams are ever
        // synced - but Jellyfin already resolved these, and they're the only
        // way to report what the video's own embedded subtitle stream(s)
        // look like. What (if anything) that implies for the sidecar's own
        // alignment strategy is the sidecar's call, not this method's.
        var embedded = subtitleStreams
            .Where(stream => stream is { Type: MediaStreamType.Subtitle, IsExternal: false })
            .ToList();

        // Bitmap-coded streams (PGS/VobSub/DVB/xsub) never enter ffsubsync's
        // own text-subtitle comparison, so a full one must not count as "a
        // good text track exists" - that would mask a forced-only text stub
        // ffsubsync's own default would otherwise correctly avoid.
        var textStreams = embedded
            .Where(stream => stream.Codec is null
                             || !BitmapSubtitleCodecs.Contains(stream.Codec, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var pgsStreams = embedded
            .Where(stream => stream.Codec is not null
                             && string.Equals(stream.Codec, "PGSSUB", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // ffsubsync's --reference-stream/--pgs-ref-stream take an ffmpeg
        // stream specifier's own per-type numbering (e.g. "s:1" - the second
        // subtitle-type stream in the container, text or bitmap codec alike,
        // counting from 0), which has nothing to do with MediaStream.Index
        // (a stream's absolute position among every stream in the file).
        // Reproduce that per-type order by sorting all embedded subtitle
        // streams by Index - the same order Jellyfin's own numbering follows
        // ffprobe's stream discovery in - and reporting a chosen stream's
        // rank within it instead of its raw Index.
        var embeddedByContainerOrder = embedded.OrderBy(stream => stream.Index).ToList();

        int RelativeSubtitleStreamIndex(MediaStream stream)
            => embeddedByContainerOrder.FindIndex(candidate => candidate.Index == stream.Index);

        // Priority: a good text track (report exactly which stream, below,
        // rather than leave it to ffsubsync's own unlogged, unobservable
        // duration-based pick) > an unambiguous single non-forced PGS track
        // (ffsubsync can align against it with --pgs-ref-stream, no OCR, but
        // only recommended when there's exactly one candidate - two or more
        // PGS streams means bare auto-detect isn't trustworthy, same
        // reasoning as the original forced-only-text-stub fix) > "only
        // forced text streams exist" (force audio VAD) > nothing usable.
        EmbeddedSubtitleSituation embeddedSituation;
        int? embeddedIndex;
        if (textStreams.Any(stream => !stream.IsForced))
        {
            embeddedSituation = EmbeddedSubtitleSituation.HasFullEmbeddedSubtitles;
            // Several non-forced text streams can exist (e.g. two dubbed
            // languages); the container's own default-disposition flag is a
            // real signal for "the" one, unlike picking an arbitrary index.
            // Falls back to the lowest index when zero or several streams
            // claim it, for a deterministic pick either way.
            var fullTextStreams = textStreams.Where(stream => !stream.IsForced).ToList();
            var defaultTextStreams = fullTextStreams.Where(stream => stream.IsDefault).ToList();
            embeddedIndex = RelativeSubtitleStreamIndex(defaultTextStreams is [var soleDefault]
                ? soleDefault
                : fullTextStreams.OrderBy(stream => stream.Index).First());
        }
        else if (pgsStreams is [{ IsForced: false }])
        {
            embeddedSituation = EmbeddedSubtitleSituation.HasFullPgsEmbeddedSubtitles;
            embeddedIndex = RelativeSubtitleStreamIndex(pgsStreams[0]);
        }
        else if (textStreams.Count > 0)
        {
            embeddedSituation = EmbeddedSubtitleSituation.HasOnlyForcedEmbeddedSubtitles;
            embeddedIndex = null;
        }
        else if (pgsStreams.Count > 1)
        {
            // Two or more PGS streams and no text track: ambiguous, same as
            // above, so this reports "no opinion" rather than the false claim
            // that no embedded subtitle exists at all.
            embeddedSituation = EmbeddedSubtitleSituation.Irrelevant;
            embeddedIndex = null;
        }
        else
        {
            embeddedSituation = EmbeddedSubtitleSituation.HasNoEmbeddedSubtitle;
            embeddedIndex = null;
        }

        return beside.Count == 0
            ? new ItemSubtitleWork(null, ItemSkipReason.NoUsableSubtitles, elsewhere)
            : new ItemSubtitleWork(
                new SubtitleSyncGroup(itemPath, beside, forced, embeddedSituation, embeddedIndex),
                ItemSkipReason.None,
                elsewhere);
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
        foreach (var candidate in group.SubtitlePaths)
        {
            if (string.Equals(candidate, subtitlePath, StringComparison.Ordinal)
                || group.ForcedSubtitlePaths?.Contains(candidate) == true)
                continue;
            if (isAlreadySynced(candidate))
                return candidate;
        }

        return group.VideoPath;
    }

    /// <summary>
    /// Dresses up <see cref="SubtitleSyncGroup.SubtitlePaths"/> with the
    /// display info and skip-cache state a subtitle/reference picker needs -
    /// shared by the "list this item's subtitles" endpoint and by the
    /// "sync just this one" endpoint's index validation, so both agree on
    /// exactly the same set of eligible subtitles. <paramref name="group"/>
    /// must have been built from <paramref name="subtitleStreams"/> (or an
    /// equivalent snapshot) - a path in <see cref="SubtitleSyncGroup.SubtitlePaths"/>
    /// with no matching stream is skipped rather than throwing, since a
    /// caller that fetched streams twice shouldn't crash over a race with
    /// the library.
    /// </summary>
    internal static IReadOnlyList<SubtitleCandidate> BuildCandidateList(
        SubtitleSyncGroup group,
        IReadOnlyList<MediaStream> subtitleStreams,
        Func<string, bool> isAlreadySynced)
    {
        var streamsByPath = subtitleStreams
            .Where(stream => !string.IsNullOrEmpty(stream.Path))
            .GroupBy(stream => stream.Path!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var candidates = new List<SubtitleCandidate>(group.SubtitlePaths.Count);
        foreach (var path in group.SubtitlePaths)
        {
            if (!streamsByPath.TryGetValue(path, out var stream))
                continue;

            candidates.Add(new SubtitleCandidate(
                stream.Index,
                path,
                stream.Language,
                stream.Title,
                group.ForcedSubtitlePaths?.Contains(path) == true,
                isAlreadySynced(path)));
        }

        return candidates;
    }
}