namespace Jellyfin.Subsync.Starter.Domain;

/// <summary>
/// One subtitle Jellyfin has already resolved as eligible to sync for an
/// item - a member of <see cref="SubtitleSyncGroup.SubtitlePaths"/>, dressed
/// up with the display info and skip/fail-cache state a subtitle/reference
/// picker needs. Identified by <see cref="Index"/> (the underlying
/// MediaStream's index) rather than <see cref="Path"/>, so a client can name
/// one without round-tripping a filesystem path back to the server.
/// </summary>
/// <param name="HasFailed">
/// True if this file's current content has failed to sync at least once
/// (<see cref="Infrastructure.IFailCache.HasPriorFailure"/>), regardless of
/// whether it has hit the consecutive-failure cap. Distinct from
/// <see cref="IsAlreadySynced"/> being false, which by itself only means
/// "not currently recorded as synced" - true for a subtitle that's never
/// been attempted at all just as much as one that failed every time.
/// </param>
internal sealed record SubtitleCandidate(
    int Index,
    string Path,
    string? Language,
    string? Title,
    bool IsForced,
    bool IsAlreadySynced,
    bool HasFailed);

/// <summary>
/// One embedded, non-forced subtitle stream a video carries, eligible to be
/// manually picked as a sync reference from the Sync tab's single-item
/// picker in place of the plugin's own disposition-tag-driven guess. See
/// <see cref="Infrastructure.SubtitleWorkBuilder.BuildEmbeddedSubtitleCandidates"/>.
/// </summary>
/// <param name="Index">The underlying MediaStream's raw, container-wide index - not its per-type rank.</param>
/// <param name="IsPgs">
/// True for a PGS (image-based) stream - the caller must resolve a pick of
/// one of these to <see cref="JellyfinReportedSituation.HasFullPgsEmbeddedSubtitles"/>,
/// never <see cref="JellyfinReportedSituation.HasFullEmbeddedSubtitles"/>, or
/// ffsubsync gets pointed at it with the wrong flag entirely. Only ever true
/// when <see cref="Configuration.PluginConfiguration.EnablePgsSupport"/> is
/// on - see <see cref="Infrastructure.SubtitleWorkBuilder.BuildEmbeddedSubtitleCandidates"/>.
/// </param>
internal sealed record EmbeddedSubtitleCandidate(int Index, string? Language, string? Title, bool IsPgs);

/// <summary>
/// One embedded audio stream a video carries, eligible to be manually picked
/// as a sync reference from the Sync tab's single-item picker. See
/// <see cref="Infrastructure.SubtitleWorkBuilder.BuildEmbeddedAudioCandidates"/>.
/// </summary>
/// <param name="Index">The underlying MediaStream's raw, container-wide index - not its per-type rank.</param>
internal sealed record EmbeddedAudioCandidate(
    int Index,
    string? Language,
    string? Title,
    string? Codec,
    int? Channels,
    string? ChannelLayout);