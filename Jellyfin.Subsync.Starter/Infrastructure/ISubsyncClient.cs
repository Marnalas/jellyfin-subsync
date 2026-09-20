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
    /// <param name="jellyfinReportedSituation">
    /// What Jellyfin reports about the video's own embedded subtitle
    /// stream(s) - a fact from Jellyfin's data, not an instruction - or,
    /// for <see cref="JellyfinReportedSituation.AttemptOnFailed"/>, an opt-in
    /// request that this attempt follows a prior failure, which takes
    /// priority over the embedded-subtitle fact. What (if anything) either
    /// implies for the sidecar's own alignment strategy is the sidecar's
    /// call, not this plugin's. <see cref="JellyfinReportedSituation.Irrelevant"/>
    /// (the default) when <paramref name="referenceFilename"/> isn't the
    /// video, where it has no bearing on anything the sidecar does.
    /// </param>
    /// <param name="referenceStreamIndex">
    /// The specific stream <paramref name="jellyfinReportedSituation"/>
    /// refers to, when it's one of <see cref="JellyfinReportedSituation.HasFullEmbeddedSubtitles"/>,
    /// <see cref="JellyfinReportedSituation.HasFullPgsEmbeddedSubtitles"/> or
    /// <see cref="JellyfinReportedSituation.AttemptOnFailed"/>; null otherwise.
    /// This is the stream's 0-based rank among the video's own streams of
    /// one type only, in container order - subtitle streams (text and
    /// bitmap codecs alike) for the first two situations, audio streams for
    /// the third - not its <c>MediaStream.Index</c> among every stream in
    /// the file. Same posture as the situation itself - a fact or request,
    /// not an instruction; what the sidecar does with it is its own call.
    /// </param>
    Task<SyncOutcome> SyncAndWaitAsync(
        PluginConfiguration config,
        string folder,
        string referenceFilename,
        string subtitleFilename,
        CancellationToken cancellationToken,
        JellyfinReportedSituation jellyfinReportedSituation = JellyfinReportedSituation.Irrelevant,
        int? referenceStreamIndex = null);
}