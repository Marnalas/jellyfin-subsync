namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// The persistence shape shared by <see cref="SkipCache"/> (tracks which
/// subtitle files have already been synced) and <see cref="FailCache"/>
/// (tracks consecutive sync failures per subtitle file). Both key their
/// entries by subtitle path, gate them on the file's current content hash,
/// and batch their writes the same way - only what counts as "cached"
/// differs between them, so each implements every method with its own
/// meaning rather than throwing. <see cref="ISkipCache"/> and
/// <see cref="IFailCache"/> exist purely so DI can bind two distinct
/// singletons of this one shape.
/// </summary>
public interface ICache : IDisposable
{
    /// <summary>
    /// True if this file's current content is already recorded by the
    /// cache. For <see cref="SkipCache"/> that means already synced; for
    /// <see cref="FailCache"/> it means failed at least the configured
    /// number of consecutive times. A content change always makes this
    /// false again, even if the path was previously recorded.
    /// </summary>
    bool IsCached(string subtitlePath);

    /// <summary>
    /// Records the file's current content against the cache. For
    /// <see cref="SkipCache"/> this marks it synced; for
    /// <see cref="FailCache"/> this extends (or, after a content change,
    /// restarts) its consecutive-failure streak.
    /// </summary>
    void AddToCache(string subtitlePath);

    /// <summary>
    /// Persists whatever <see cref="AddToCache"/> has batched up. Called at
    /// the end of a sweep, including a canceled one.
    /// </summary>
    void Flush();

    /// <summary>
    /// Drops entries whose file no longer exists. Returns how many were
    /// removed. Call it at the end of a sweep, not the start - see the
    /// implementation for why.
    /// </summary>
    int RemoveMissingFiles();

    /// <summary>Removes every tracked entry. Returns how many were removed.</summary>
    int Clear();

    /// <summary>Removes the given path if present.</summary>
    void RemoveForPath(string subtitlePath);

    /// <summary>Removes the given paths if present. Returns how many were actually removed.</summary>
    int RemoveForPaths(IEnumerable<string> subtitlePaths);
}

/// <summary>
/// Marker for DI: resolves to the <see cref="SkipCache"/> singleton. See
/// <see cref="ICache"/> for the actual method surface.
/// </summary>
public interface ISkipCache : ICache;

/// <summary>
/// Marker for DI: resolves to the <see cref="FailCache"/> singleton. See
/// <see cref="ICache"/> for the actual method surface.
/// </summary>
public interface IFailCache : ICache;