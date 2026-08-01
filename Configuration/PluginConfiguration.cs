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
        /// container, to its equivalent path as seen by the subsync-sidecar
        /// container. Each pair is independent - there's no shared root
        /// requirement, so libraries can be mounted under completely
        /// different directory layouts on each side.
        /// </summary>
        /// <remarks>
        /// A List of entries is used instead of a Dictionary because
        /// Jellyfin persists plugin configuration via XmlSerializer, which
        /// cannot serialize types that merely implement IDictionary.
        /// </remarks>
        public List<PathMapEntry> WatchedPathsMaps { get; set; } = [];

        /// <summary>Video file extensions to consider when matching a subtitle to its video.</summary>
        public List<string> VideoExtensions { get; set; } = ["mkv", "mp4", "m4v", "avi", "ts", "mov", "wmv"];

        /// <summary>How often the sidecar job status is polled while waiting for a sync to finish.</summary>
        public int PollIntervalMilliseconds { get; set; } = 3000;

        /// <summary>Max time to wait for a single sync job before giving up.</summary>
        public int JobTimeoutSeconds { get; set; } = 1800;

        /// <summary>
        /// How many subtitle files the sweep task submits to the sidecar at
        /// once. Defaults to 1. Raising this only
        /// helps if the sidecar's own MAX_PARALLEL_JOBS is also raised (it
        /// defaults to cpu_count - 1) - otherwise the sidecar just queues
        /// the extra submissions and runs them one at a time anyway.
        /// </summary>
        public int MaxParallelJobs { get; set; } = 1;
    }
}
