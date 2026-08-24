namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Tracks which subtitle files have already been synced, so repeat sweeps
/// skip what's already up to date.
/// </summary>
public interface ISkipCache : IDisposable
{
    /// <summary>Returns true if this exact file content has already been synced.</summary>
    bool IsAlreadySynced(string subtitlePath);

    /// <summary>Records the current content of the subtitle file as "synced".</summary>
    void MarkSynced(string subtitlePath);

    /// <summary>
    /// Persists whatever <see cref="MarkSynced"/> has batched up. Called at
    /// the end of a sweep, including a canceled one.
    /// </summary>
    void Flush();

    /// <summary>
    /// Drops entries whose file no longer exists. Returns how many were
    /// removed. Call it at the end of a sweep, not the start - see the
    /// implementation for why.
    /// </summary>
    int RemoveMissingFiles();
}

