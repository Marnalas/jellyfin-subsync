using System;
using System.IO;
using System.Linq;
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
        /// pair the sidecar expects, translating the mount-point prefix
        /// (JellyfinMediaRoot -> SidecarMediaRoot) along the way.
        /// </summary>
        public static (string Folder, string Filename) ToSidecarRelative(string absolutePath, PluginConfiguration config)
        {
            var dir = Path.GetDirectoryName(absolutePath) ?? string.Empty;
            var filename = Path.GetFileName(absolutePath);

            var root = config.JellyfinMediaRoot.TrimEnd('/');
            var relative = dir.StartsWith(root, StringComparison.Ordinal)
                ? dir[root.Length..].TrimStart('/')
                : dir.TrimStart('/');

            return (relative, filename);
        }
    }
}
