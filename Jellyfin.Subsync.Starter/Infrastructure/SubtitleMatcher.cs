using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    internal static class SubtitleMatcher
    {
        private const string SyncedTempSuffix = "_synced_temp";
        private const string OriginalBackupSuffix = "_original_backup";

        /// <summary>
        /// Walks every watched path directory by directory and, within each
        /// directory, groups its subtitle files by the video file they belong to
        /// (same base name, e.g. "Movie.eng.srt" and "Movie.rus.srt" both
        /// belong to "Movie"). Yields one group at a time so a directory's
        /// handful of subtitles is buffered, never the whole library.
        /// </summary>
        internal static IEnumerable<IReadOnlyList<string>> EnumerateSubtitleGroups(List<string> paths, PluginConfiguration config, ILogger logger)
        {
            for (var i = 0; i < paths.Count; ++i)
            {
                var root = paths[i];
                if (!Directory.Exists(root))
                {
                    logger.LogWarning("Subsync sweep: path does not exist, skipping: {Path}", root);
                    continue;
                }

                using var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Prepend(root)
                    .GetEnumerator();

                while (true)
                {
                    string directory;
                    try
                    {
                        if (!directories.MoveNext())
                        {
                            break;
                        }

                        directory = directories.Current;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Subsync sweep: failed to enumerate {Path}", root);
                        break;
                    }

                    List<string> subtitlesInDirectory;
                    try
                    {
                        subtitlesInDirectory = [..
                            Directory.EnumerateFiles(directory,"*", SearchOption.TopDirectoryOnly)
                                .Where(path => IsSubtitleFile(path, config))];
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Subsync sweep: failed to list {Path}", directory);
                        continue;
                    }

                    foreach (var group in
                        subtitlesInDirectory
                            .GroupBy(path => GetBaseName(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase))
                    {
                        yield return group.ToList();
                    }
                }
            }
        }

        /// <summary>
        /// Given a subtitle path, finds the matching video file and other
        /// subtiles in the same directory. Handles both "Movie.mkv" +
        /// "Movie.rus.srt" (language tagged) and "Movie.mkv" + "Movie.srt"
        /// naming, for any configured SubtitleExtensions.
        /// </summary>
        internal static IEnumerable<RelatedFile>? FindRelatedFiles(string subtitlePath, PluginConfiguration config)
        {
            var dir = Path.GetDirectoryName(subtitlePath);
            if (dir is null)
            {
                return null;
            }

            return FindRelatedFilesCore(subtitlePath, dir, config);
        }

        internal static IEnumerable<RelatedFile> FindRelatedFilesCore(string subtitlePath, string dir, PluginConfiguration config)
        {
            var subtitleName = Path.GetFileName(subtitlePath);
            var baseName = GetBaseName(subtitleName);

            foreach (var ext in config.VideoExtensions)
            {
                var candidate = Path.Combine(dir, $"{baseName}.{ext}");
                if (File.Exists(candidate))
                {
                    yield return new RelatedFile
                    {
                        Type = FileType.Movie,
                        FilePath = candidate
                    };
                }
            }

            foreach (var candidate in Directory.EnumerateFiles(dir))
            {
                var candidateName = Path.GetFileName(candidate);
                if (string.Equals(candidateName, subtitleName, StringComparison.Ordinal)
                    || !IsSubtitleFile(candidate, config))
                {
                    continue;
                }

                if (!string.Equals(GetBaseName(candidateName), baseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new RelatedFile
                {
                    Type = FileType.Subtitle,
                    FilePath = candidate
                };
            }
        }

        /// <summary>
        /// Strips a file's language/track tag and extension down to the
        /// shared root used to group a movie with its subtitles (e.g. both
        /// "Movie.mkv" and "Movie.rus.srt" reduce to "Movie"). Also used by
        /// SyncLibrarySweepTask to bucket a directory's subtitles by the
        /// movie they belong to, so siblings can be synced one at a time.
        /// </summary>
        internal static string GetBaseName(string fileName)
        {
            var match = RegularExpressions.RootPart().Match(fileName);
            return match.Success
                ? match.Groups["root"].Value
                : Path.GetFileNameWithoutExtension(fileName);
        }

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
            {
                return false;
            }

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

                if (isMatch && (bestJellyfinRoot is null || jellyfinRoot.Length > bestJellyfinRoot.Length))
                {
                    bestJellyfinRoot = jellyfinRoot;
                    bestSidecarRoot = entry.SidecarPath.TrimEnd('/');
                }
            }

            if (bestJellyfinRoot is null)
            {
                return null;
            }

            var relative = dir[bestJellyfinRoot.Length..].TrimStart('/');
            var sidecarDir = relative.Length == 0 ? bestSidecarRoot! : $"{bestSidecarRoot}/{relative}";
            return (sidecarDir, filename);
        }
    }
}
