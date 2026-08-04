namespace Jellyfin.Subsync.Starter.Domain
{
    /// <summary>
    /// One video item and every external subtitle file Jellyfin has indexed for
    /// it that the plugin is willing to sync. All paths are Jellyfin-side
    /// absolutes, and every entry in <see cref="SubtitlePaths"/> lives in the
    /// same directory as <see cref="VideoPath"/> - the sidecar's /sync takes a
    /// single folder plus two filenames, so a cross-directory pair can't be
    /// expressed.
    /// </summary>
    internal sealed record SubtitleSyncGroup(
        string VideoPath,
        IReadOnlyList<string> SubtitlePaths,
        IReadOnlySet<string>? ForcedSubtitlePaths = null);

    /// <summary>
    /// Why an item produced no group. Only used for logging - the sweep skips
    /// the item either way.
    /// </summary>
    internal enum ItemSkipReason
    {
        None = 0,

        /// <summary>The item has no file path, or its path has no parent directory.</summary>
        NoPath = 1,

        /// <summary>
        /// An ISO, BDMV or VIDEO_TS rip: there is no single elementary video
        /// file for ffsubsync to align against.
        /// </summary>
        PathIsDiscImageOrFolder = 2,

        /// <summary>Nothing survived the stream filters.</summary>
        NoUsableSubtitles = 3
    }

    /// <summary>
    /// The result of turning one library item into sync work.
    /// <see cref="SubtitlesInOtherDirectories"/> is the only skip category
    /// surfaced to the caller, because it is the only user-actionable one;
    /// embedded streams, unconfigured extensions and the sidecar's own
    /// byproducts are dropped silently.
    /// </summary>
    internal sealed record ItemSubtitleWork(
        SubtitleSyncGroup? Group,
        ItemSkipReason Reason,
        IReadOnlyList<string> SubtitlesInOtherDirectories);
}
