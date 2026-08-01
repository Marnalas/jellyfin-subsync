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
        /// Absolute paths (as seen INSIDE the Jellyfin container) to scan/watch.
        /// These must correspond to your library mount points, e.g.
        /// "/media/films", "/media/films4k", "/media/series", etc.
        /// </summary>
        public List<string> WatchedPaths { get; set; } = new()
        {
            "/media/films",
            "/media/films4k",
            "/media/series",
            "/media/series4k",
            "/media/animes",
            "/media/animes4k",
        };

        /// <summary>
        /// Base path as seen by the SIDECAR container (may differ from
        /// WatchedPaths above, since Jellyfin and the sidecar can mount the
        /// same host directories at different in-container paths).
        /// The plugin translates a Jellyfin-side path to a sidecar-side
        /// "folder" argument using this prefix swap.
        /// </summary>
        public string JellyfinMediaRoot { get; set; } = "/media";

        public string SidecarMediaRoot { get; set; } = "/mnt/media";

        /// <summary>Video file extensions to consider when matching a subtitle to its video.</summary>
        public List<string> VideoExtensions { get; set; } = new() { "mkv", "mp4", "m4v", "avi", "ts", "mov", "wmv" };

        /// <summary>How often the sidecar job status is polled while waiting for a sync to finish.</summary>
        public int PollIntervalMilliseconds { get; set; } = 3000;

        /// <summary>Max time to wait for a single sync job before giving up.</summary>
        public int JobTimeoutSeconds { get; set; } = 1800;
    }
}
