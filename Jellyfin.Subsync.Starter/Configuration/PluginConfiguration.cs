using MediaBrowser.Model.Plugins;

namespace Jellyfin.Subsync.Starter.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Base URL of the subsync-sidecar service, e.g. http://subsync-sidecar:8000
        /// (use the compose service name so it resolves on the docker network).
        /// </summary>
        public string SidecarUrl { get; set; } = "http://subsync-sidecar:8000";

        /// <summary>
        /// Maps each library path, as seen INSIDE the Jellyfin container, to
        /// its equivalent path as seen by the subsync-sidecar container. Each
        /// pair is independent - there's no shared root requirement, so
        /// libraries can be mounted under completely different directory
        /// layouts on each side. This is path translation only: what gets
        /// swept comes from the Jellyfin library, not from these entries. A
        /// subtitle whose path matches no entry is skipped with a warning,
        /// because the sidecar would have no way to find it.
        /// </summary>
        /// <remarks>
        /// A List of entries is used instead of a Dictionary because
        /// Jellyfin persists plugin configuration via XmlSerializer, which
        /// cannot serialize types that merely implement IDictionary.
        /// </remarks>
        public List<PathMapEntry> WatchedPathsMaps { get; set; } = [];

        /// <summary>Subtitle file extensions to sync. Narrows the set Jellyfin already recognises.</summary>
        public List<string> SubtitleExtensions { get; set; } = [];

        /// <summary>How often the sidecar job status is polled while waiting for a sync to finish.</summary>
        public int PollIntervalMilliseconds { get; set; } = 3000;

        /// <summary>
        /// Max time a single sync may spend actually running on the sidecar.
        /// Sent to the sidecar with the job, which enforces it and stops
        /// ffsubsync at that point; this side deliberately waits a little
        /// longer, so the sidecar is always the one to declare the timeout.
        /// Time the job spends queued behind other jobs is NOT charged against
        /// this - see QueueWaitTimeoutSeconds.
        /// </summary>
        public int JobTimeoutSeconds { get; set; } = 1800;

        /// <summary>
        /// Max time a submitted job may sit queued on the sidecar before this
        /// side gives up and cancels it. Hitting this means more work is being
        /// submitted than the sidecar has workers for - compare MaxParallelJobs
        /// below with the sidecar's MAX_PARALLEL_JOBS. 0 waits indefinitely.
        /// </summary>
        public int QueueWaitTimeoutSeconds { get; set; } = 3600;

        /// <summary>
        /// Timeout for one individual HTTP call to the sidecar (submit, poll,
        /// cancel) - not for the sync job as a whole. Guards against a poll
        /// hanging on a half-open connection; HttpClient's own default of 100s
        /// applies per call too, this just makes it explicit and shorter.
        /// </summary>
        public int SidecarRequestTimeoutSeconds { get; set; } = 30;

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
