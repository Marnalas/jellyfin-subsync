using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Subsync.Starter.Configuration
{
    /// <summary>
    /// Plugin configuration. Editable by hand via the plugin's config XML
    /// under your Jellyfin config volume (plugins/configurations/) until a
    /// proper admin web UI page is added.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Base URL of the subsync-sidecar service, e.g. http://subsync-sidecar:8000
        /// (use the compose service name so it resolves on the docker network).
        /// </summary>
        public string SidecarUrl { get; set; } = "http://subsync-sidecar:8000";

        /// <summary>
        /// Maps each watched library path, as seen INSIDE the Jellyfin
        /// container (the key), to its equivalent path as seen by the
        /// subsync-sidecar container (the value). Each pair is independent -
        /// there's no shared root requirement, so libraries can be mounted
        /// under completely different directory layouts on each side.
        /// </summary>
        public Dictionary<string, string> WatchedPathsMaps { get; set; } = new()
        {
            ["/media/films"] = "/mnt/media/films",
            ["/media/films4k"] = "/mnt/media/films4k",
            ["/media/series"] = "/mnt/media/series",
            ["/media/series4k"] = "/mnt/media/series4k",
            ["/media/animes"] = "/mnt/media/animes",
            ["/media/animes4k"] = "/mnt/media/animes4k",
        };

        /// <summary>Video file extensions to consider when matching a subtitle to its video.</summary>
        public List<string> VideoExtensions { get; set; } = new() { "mkv", "mp4", "m4v", "avi", "ts", "mov", "wmv" };

        /// <summary>How often the sidecar job status is polled while waiting for a sync to finish.</summary>
        public int PollIntervalMilliseconds { get; set; } = 3000;

        /// <summary>Max time to wait for a single sync job before giving up.</summary>
        public int JobTimeoutSeconds { get; set; } = 1800;
    }
}
