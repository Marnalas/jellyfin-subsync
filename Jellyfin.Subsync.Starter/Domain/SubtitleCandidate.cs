namespace Jellyfin.Subsync.Starter.Domain;

/// <summary>
/// One subtitle Jellyfin has already resolved as eligible to sync for an
/// item - a member of <see cref="SubtitleSyncGroup.SubtitlePaths"/>, dressed
/// up with the display info and skip-cache state a subtitle/reference picker
/// needs. Identified by <see cref="Index"/> (the underlying MediaStream's
/// index) rather than <see cref="Path"/>, so a client can name one without
/// round-tripping a filesystem path back to the server.
/// </summary>
internal sealed record SubtitleCandidate(
    int Index,
    string Path,
    string? Language,
    string? Title,
    bool IsForced,
    bool IsAlreadySynced);