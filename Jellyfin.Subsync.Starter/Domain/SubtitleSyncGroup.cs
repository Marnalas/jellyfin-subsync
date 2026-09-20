namespace Jellyfin.Subsync.Starter.Domain;

/// <summary>
/// A fact - or, for <see cref="AttemptOnFailed"/>, an opt-in request - that the
/// plugin reports about a sync attempt. Not an instruction: what (if
/// anything) it implies for how the sidecar should align against the video
/// is entirely the sidecar's call, not the plugin's. Only meaningful when
/// the video itself is being used as the sync reference; see
/// <see cref="Application.SubtitleSyncOrchestrator"/>.
/// </summary>
public enum JellyfinReportedSituation
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
    HasFullPgsEmbeddedSubtitles = 4,

    /// <summary>
    /// This isn't a first attempt: the subtitle being synced already failed
    /// at least once with its current content, and the plugin's "attempt
    /// fallback on failed" setting is on. Never produced by
    /// <see cref="Infrastructure.SubtitleWorkBuilder.BuildWork"/> - only
    /// <see cref="Application.SubtitleSyncOrchestrator"/> can see fail-cache
    /// state - and takes priority over whatever embedded-subtitle situation
    /// would otherwise have been reported for this attempt. Unlike every
    /// other non-<see cref="Irrelevant"/> value, its accompanying
    /// <c>SubtitleSyncGroup.ReferenceStreamIndex</c> is an <em>audio</em>
    /// stream's rank, not a subtitle stream's.
    /// </summary>
    AttemptOnFailed = 5
}

/// <summary>
/// One video item and every external subtitle file Jellyfin has indexed for
/// it that the plugin is willing to sync. All paths are Jellyfin-side
/// absolutes, and every entry in <see cref="SubtitlePaths"/> lives in the
/// same directory as <see cref="VideoPath"/> - the sidecar's /sync takes a
/// single folder plus two filenames, so a cross-directory pair can't be
/// expressed.
/// </summary>
/// <param name="ReferenceStreamIndex">
/// The specific embedded subtitle stream that justified
/// <see cref="JellyfinReportedSituation.HasFullEmbeddedSubtitles"/> or
/// <see cref="JellyfinReportedSituation.HasFullPgsEmbeddedSubtitles"/> -
/// null for every other situation <see cref="Infrastructure.SubtitleWorkBuilder.BuildWork"/>
/// itself ever produces. Not <c>MediaStream.Index</c> (the stream's absolute
/// position among every stream in the file); this is its 0-based rank among
/// the video's own embedded subtitle streams only, text and bitmap codecs
/// alike, in container order - the same numbering an ffmpeg stream
/// specifier's per-type index means (what "s:1" in "0:s:1" refers to). Like
/// the situation itself, a fact about the item for the sidecar to interpret,
/// not an instruction.
/// </param>
/// <param name="FallbackAudioStreamIndex">
/// Jellyfin's own container-order rank (same numbering as
/// <paramref name="ReferenceStreamIndex"/>, but among the video's own audio
/// streams, not subtitle ones - what "a:2" in "0:a:2" refers to) of the
/// best audio stream to retry against: the container's own default-flagged
/// stream if one exists, same signal already used to pick a default text
/// subtitle stream above, else the lowest-bitrate stream. Null only when
/// the video has no audio stream at all. Always computed by
/// <see cref="Infrastructure.SubtitleWorkBuilder.BuildWork"/>
/// alongside the subtitle-derived facts above, but only ever read by
/// <see cref="Application.SubtitleSyncOrchestrator"/> when it reports
/// <see cref="JellyfinReportedSituation.AttemptOnFailed"/> instead of this
/// record's own <see cref="JellyfinReportedSituation"/>.
/// </param>
internal sealed record SubtitleSyncGroup(
    string VideoPath,
    IReadOnlyList<string> SubtitlePaths,
    IReadOnlySet<string>? ForcedSubtitlePaths = null,
    JellyfinReportedSituation JellyfinReportedSituation = JellyfinReportedSituation.Irrelevant,
    int? ReferenceStreamIndex = null,
    int? FallbackAudioStreamIndex = null);

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