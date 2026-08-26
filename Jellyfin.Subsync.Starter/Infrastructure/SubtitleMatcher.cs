using Jellyfin.Subsync.Starter.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Subsync.Starter.Infrastructure;

internal static class SubtitleMatcher
{
    private const string SyncedTempSuffix = "_synced_temp";
    private const string OriginalBackupSuffix = "_original_backup";

    /// <summary>
    /// Every external subtitle path Jellyfin currently associates with this
    /// item - not filtered through the plugin's configured
    /// SubtitleExtensions, so a later config change can't leave stale
    /// skip-cache entries this can no longer reach. Shared by every caller
    /// that needs to find or clear an item's cached subtitles, so the set
    /// they all agree on can't drift apart.
    /// </summary>
    internal static IEnumerable<string>
        GetExternalSubtitlePaths(BaseItem item, IMediaSourceManager mediaSourceManager) =>
        GetExternalSubtitlePaths(
            mediaSourceManager.GetMediaStreams(new MediaStreamQuery
                { ItemId = item.Id, Type = MediaStreamType.Subtitle }));

    /// <inheritdoc cref="GetExternalSubtitlePaths(BaseItem, IMediaSourceManager)"/>
    /// <remarks>
    /// Takes already-fetched streams so a caller that also needs them for
    /// something else (like building sync work) doesn't have to query
    /// Jellyfin for the same item's streams twice.
    /// </remarks>
    internal static IEnumerable<string> GetExternalSubtitlePaths(IEnumerable<MediaStream> subtitleStreams) =>
        subtitleStreams
            .Where(stream => stream.IsExternal && stream.IsExternalUrl != true && !string.IsNullOrEmpty(stream.Path))
            .Select(stream => stream.Path!)
            .Distinct();

    /// <summary>
    /// True if <paramref name="path"/>'s extension is a configured
    /// SubtitleExtensions entry and it isn't one of the sidecar's own
    /// temp/backup byproduct files (which carry the same extension as
    /// the subtitle they were derived from).
    /// </summary>
    internal static bool IsSubtitleFile(string path, PluginConfiguration config)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!config.SubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return false;

        var stem = Path.GetFileNameWithoutExtension(path);
        return !stem.EndsWith(SyncedTempSuffix, StringComparison.OrdinalIgnoreCase)
               && !stem.EndsWith(OriginalBackupSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a Jellyfin-side absolute path into the (folder, filename)
    /// pair the sidecar expects, by finding which WatchedPathsMaps entry
    /// the path falls under and re-rooting the directory from that
    /// entry's Jellyfin-side key onto its sidecar-side value (e.g.
    /// "/media/SeriesLibrary/Show" -&gt; "/mnt/media/SeriesLibrary/Show").
    /// Picks the longest matching key so overlapping roots resolve to the
    /// right entry regardless of dictionary iteration order. Returns null
    /// if the path isn't under any configured entry.
    /// </summary>
    internal static (string Folder, string Filename)? ToSidecarAbsolute(string absolutePath, PluginConfiguration config)
    {
        var dir = Path.GetDirectoryName(absolutePath) ?? string.Empty;
        var filename = Path.GetFileName(absolutePath);

        string? bestJellyfinRoot = null;
        string? bestSidecarRoot = null;

        foreach (var entry in config.WatchedPathsMaps)
        {
            var jellyfinRoot = entry.JellyfinPath.TrimEnd('/');
            var isMatch = dir.Equals(jellyfinRoot, StringComparison.Ordinal)
                          || dir.StartsWith(jellyfinRoot + "/", StringComparison.Ordinal);

            if (!isMatch || (bestJellyfinRoot is not null && jellyfinRoot.Length <= bestJellyfinRoot.Length))
                continue;
            bestJellyfinRoot = jellyfinRoot;
            bestSidecarRoot = entry.SidecarPath.TrimEnd('/');
        }

        if (bestJellyfinRoot is null)
            return null;

        var relative = dir[bestJellyfinRoot.Length..].TrimStart('/');
        var sidecarDir = relative.Length == 0 ? bestSidecarRoot! : $"{bestSidecarRoot}/{relative}";
        return (sidecarDir, filename);
    }
}