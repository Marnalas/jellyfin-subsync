using MediaBrowser.Controller.Library;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Tells Jellyfin's file system watcher to ignore a folder while the
/// sidecar rewrites a subtitle in it, so the write doesn't queue a library
/// refresh whose subtitle-fetch step re-downloads the file we just synced.
/// </summary>
public interface IFolderChangeSuppressor
{
    /// <summary>
    /// Suppresses watcher-triggered refreshes for <paramref name="folder"/>
    /// until the returned scope is disposed.
    /// </summary>
    IDisposable Suppress(string folder);
}

/// <summary>
/// Ref-counts suppression per folder on top of <see cref="ILibraryMonitor"/>.
/// Jellyfin's own ignore-set is a flat dictionary keyed by path, not
/// reference-counted: two subtitles synced concurrently out of the same
/// folder (e.g. two episodes in one season) would otherwise let whichever
/// job finishes first re-arm the watcher while its sibling is still
/// writing. Counting per folder here keeps the folder suppressed until
/// every concurrent job in it has finished.
/// </summary>
internal sealed class FolderChangeSuppressor(ILibraryMonitor libraryMonitor) : IFolderChangeSuppressor
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _refCounts = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Suppress(string folder)
    {
        lock (_gate)
        {
            if (_refCounts.TryGetValue(folder, out var count))
                _refCounts[folder] = count + 1;
            else
            {
                _refCounts[folder] = 1;
                libraryMonitor.ReportFileSystemChangeBeginning(folder);
            }
        }

        return new Scope(this, folder);
    }

    private void Release(string folder)
    {
        var last = false;
        lock (_gate)
        {
            var count = _refCounts[folder] - 1;
            if (count <= 0)
            {
                _refCounts.Remove(folder);
                last = true;
            }
            else
                _refCounts[folder] = count;
        }

        // refreshPath: false - nothing needs a metadata refresh, the sidecar
        // only rewrites subtitle bytes, it never adds/removes/moves media.
        if (last)
            libraryMonitor.ReportFileSystemChangeComplete(folder, refreshPath: false);
    }

    private sealed class Scope(FolderChangeSuppressor owner, string folder) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.Release(folder);
        }
    }
}

