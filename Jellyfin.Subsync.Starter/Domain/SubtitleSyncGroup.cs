namespace Jellyfin.Subsync.Starter.Domain;

/// <summary>
/// What Jellyfin reports about a video's own embedded subtitle stream(s).
/// A fact about the item, not an instruction - what (if anything) it implies
/// for how the sidecar should align against the video is entirely the
/// sidecar's call, not the plugin's. Only meaningful when the video itself
/// is being used as the sync reference; see <see cref="Application.SubtitleSyncOrchestrator"/>.
/// </summary>
public enum EmbeddedSubtitleSituation
{
    /// <summary>
    /// The default. Either this hasn't been computed (a group built outside
    /// <see cref="Infrastructure.SubtitleWorkBuilder.BuildWork"/>), or the
    /// video isn't the sync reference, so the situation doesn't apply to
    /// this call at all - the sidecar always treats this the same as "no
    /// opinion".
    /// </summary>
    Irrelevant = 0,

    /// <summary>No embedded subtitle stream at all.</summary>
    HasNoEmbeddedSubtitle = 1,

    /// <summary>At least one embedded subtitle stream is a full, non-forced track.</summary>
    HasFullEmbeddedSubtitles = 2,

    /// <summary>Every embedded subtitle stream that exists is forced (a "forced-only stub").</summary>
    HasOnlyForcedEmbeddedSubtitles = 3,

    /// <summary>
    /// No full text-based embedded stream exists, but exactly one embedded
    /// PGS (image-based) subtitle stream does, and it isn't forced -
    /// unambiguous enough to recommend as a reference. ffsubsync can align
    /// against it via packet-display timing with no OCR; see ffsubsync's
    /// --pgs-ref-stream.
    /// </summary>
    HasFullPgsEmbeddedSubtitles = 4
}

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
    IReadOnlySet<string>? ForcedSubtitlePaths = null,
    EmbeddedSubtitleSituation EmbeddedSubtitleSituation = EmbeddedSubtitleSituation.Irrelevant);

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