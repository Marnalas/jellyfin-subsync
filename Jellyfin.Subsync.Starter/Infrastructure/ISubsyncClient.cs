using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// How a sync attempt ended. Richer than a bool because the caller needs to
/// tell "this file can't be synced" from "the sidecar is in trouble" - only
/// the first is worth recording against the file, and only the second says
/// anything about whether the rest of the sweep is worth attempting.
/// </summary>
public enum SyncOutcome
{
    /// <summary>The sidecar reported the subtitle synced and replaced.</summary>
    Synced,

    /// <summary>The sidecar ran the job and it failed. Retrying it right now won't help.</summary>
    Failed,

    /// <summary>Never got a worker slot within the queue-wait budget. Cancelled.</summary>
    QueueTimedOut,

    /// <summary>Ran past its budget without a terminal answer. Cancelled.</summary>
    RunTimedOut,

    /// <summary>The sidecar no longer knows this job id - it restarted, or retired the entry.</summary>
    JobUnknown,

    /// <summary>Couldn't submit, or polling failed repeatedly. Says nothing about this file.</summary>
    SidecarUnreachable
}

/// <summary>
/// The plugin's side of the sidecar HTTP protocol. An interface so the
/// sweep and the orchestrator can be exercised without a sidecar, and so
/// the client itself can be driven against a stubbed transport.
/// </summary>
public interface ISubsyncClient
{
    /// <summary>
    /// True if the sidecar answers /health. Used once at the start of a
    /// sweep - an unreachable sidecar should fail loudly there rather than
    /// once per subtitle for the length of the run.
    /// </summary>
    Task<bool> IsHealthyAsync(PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    /// Submits a sync job and waits for it to reach a terminal state.
    /// Configuration is passed per call rather than held: a sweep reads it
    /// once and threads the same snapshot through every file.
    /// </summary>
    /// <param name="config">Sidecar URL and timeouts for this call.</param>
    /// <param name="folder">The sidecar-side folder both files live in.</param>
    /// <param name="referenceFilename">What to align the subtitle against.</param>
    /// <param name="subtitleFilename">The subtitle file to align and overwrite.</param>
    /// <param name="cancellationToken">Cancels the submit-and-poll round trip.</param>
    /// <param name="vad">
    /// A value for ffsubsync's own --vad flag (e.g. "webrtc"), or null to
    /// leave ffsubsync's default (or the sidecar's own FFSUBSYNC_EXTRA_ARGS
    /// configuration) alone. Set only when this plugin has determined, from
    /// Jellyfin's stream metadata, that the default isn't safe for this
    /// particular job - see <see cref="SubtitleSyncGroup.EmbeddedSubtitleIsForcedOnlyStub"/>.
    /// </param>
    Task<SyncOutcome> SyncAndWaitAsync(
        PluginConfiguration config,
        string folder,
        string referenceFilename,
        string subtitleFilename,
        CancellationToken cancellationToken,
        string? vad = null);
}