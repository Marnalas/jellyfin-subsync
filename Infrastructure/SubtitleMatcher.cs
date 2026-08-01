using Jellyfin.Subsync.Starter.Configuration;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    public static class SubtitleMatcher
    {
        /// <summary>
        /// Given a subtitle path, finds the matching video file in the same
        /// directory. Handles both "Movie.mkv" + "Movie.rus.srt" (language
        /// tagged) and "Movie.mkv" + "Movie.srt" naming.
        /// </summary>
        public static string? FindMovieFile(string subtitlePath, PluginConfiguration config)
        {
            var dir = Path.GetDirectoryName(subtitlePath);
            if (dir is null)
            {
                return null;
            }

            var subName = Path.GetFileName(subtitlePath);
            var noSrt = subName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                ? subName[..^4]
                : subName;
            var noLang = Path.GetFileNameWithoutExtension(noSrt); // strips one more segment, e.g. ".rus"

            foreach (var baseName in new[] { noLang, noSrt })
            {
                foreach (var ext in config.VideoExtensions)
                {
                    var candidate = Path.Combine(dir, $"{baseName}.{ext}");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        public static bool IsSubtitleFile(string path) =>
            path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("_synced_temp.srt", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("_original_backup.srt", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Splits a Jellyfin-side absolute path into the (folder, filename)
        /// pair the sidecar expects, by finding which WatchedPathsMaps entry
        /// the path falls under and re-rooting the directory from that
        /// entry's Jellyfin-side key onto its sidecar-side value (e.g.
        /// "/media/series4k/Show" -&gt; "/mnt/media/series4k/Show"). Picks the
        /// longest matching key so overlapping roots (e.g. "/media/films"
        /// vs. "/media/films4k") resolve to the right entry regardless of
        /// dictionary iteration order. Returns null if the path isn't under
        /// any configured entry.
        /// </summary>
        public static (string Folder, string Filename)? ToSidecarAbsolute(string absolutePath, PluginConfiguration config)
        {
            var dir = Path.GetDirectoryName(absolutePath) ?? string.Empty;
            var filename = Path.GetFileName(absolutePath);

            string? bestJellyfinRoot = null;
            string? bestSidecarRoot = null;

            foreach (var (jellyfinPath, sidecarPath) in config.WatchedPathsMaps)
            {
                var jellyfinRoot = jellyfinPath.TrimEnd('/');
                var isMatch = dir.Equals(jellyfinRoot, StringComparison.Ordinal)
                    || dir.StartsWith(jellyfinRoot + "/", StringComparison.Ordinal);

                if (isMatch && (bestJellyfinRoot is null || jellyfinRoot.Length > bestJellyfinRoot.Length))
                {
                    bestJellyfinRoot = jellyfinRoot;
                    bestSidecarRoot = sidecarPath.TrimEnd('/');
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
