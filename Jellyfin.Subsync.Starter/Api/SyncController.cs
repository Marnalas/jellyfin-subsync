using System.Net.Mime;
using Jellyfin.Subsync.Starter.Application;
using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Domain;
using Jellyfin.Subsync.Starter.Infrastructure;
using Jellyfin.Subsync.Starter.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Api;

/// <summary>
/// Lets an admin (re)sync one library item's subtitles on demand from the
/// "Sync" dashboard tab. Clears the item's skip-cache entries first, so a
/// subtitle the sweep already considers synced is re-attempted anyway.
/// Deliberately separate from SkipCacheController: that controller's job is
/// "forget", this one's is "forget, then immediately redo".
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Subsync/Sync")]
public class SyncController(
    ISkipCache skipCache,
    IFailCache failCache,
    ISubsyncClient client,
    IFolderChangeSuppressor suppressor,
    IPluginConfigurationProvider configurationProvider,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    ITaskManager taskManager,
    ILocalizationManager localizationManager,
    ILogger<SyncController> logger) : ControllerBase
{
    /// <remarks>
    /// <paramref name="request"/> is optional: today's UI sends no body at
    /// all for "sync everything", and an absent/no-Content-Type POST body
    /// binds a nullable <see cref="SyncItemRequest"/> to null rather than
    /// erroring, so that path is unaffected by this parameter's existence.
    /// A <see cref="SyncItemRequest.SubtitleIndex"/> narrows the request to
    /// just that one subtitle, syncing it against
    /// <see cref="SyncItemRequest.ReferenceSubtitleIndex"/> if given, else
    /// the video - see <see cref="SyncOneAsync"/>.
    /// </remarks>
    [HttpPost("{itemId:guid}")]
    public async Task<ActionResult<object>> SyncItem(
        Guid itemId,
        [FromBody] SyncItemRequest? request,
        CancellationToken cancellationToken)
    {
        // The sweep and this endpoint share no lock over the actual sync
        // call, only over the skip-cache bookkeeping around it - two
        // concurrent syncs of the same subtitle file would race the sidecar
        // into overwriting it from two directions at once. Refusing the
        // request outright is simpler and safer than trying to interleave
        // with a sweep that could be touching this exact item right now.
        if (IsSweepRunning())
            return Conflict(new
            {
                error = "A library sweep is currently running. Wait for it to finish before syncing a single item."
            });

        var item = libraryManager.GetItemById(itemId);
        if (item is null)
            return NotFound();

        var config = configurationProvider.GetSnapshot();

        // Same reasoning as the sweep: fail fast and loudly here, before
        // touching the skip-cache, rather than letting every subtitle below
        // discover this one at a time.
        if (!await SidecarHealthChecker.IsReachableAsync(client, config, logger, cancellationToken)
                .ConfigureAwait(false))
        {
            logger.LogError(
                "Subsync sync: the sidecar at {Url} did not answer /health after {Attempts} attempts, aborting the sync for {Item}",
                config.SidecarUrl, SidecarHealthChecker.Attempts, item.Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = $"The subsync sidecar at {config.SidecarUrl} is not reachable."
            });
        }

        IReadOnlyList<MediaStream> subtitleStreams;
        IReadOnlyList<MediaStream> audioStreams;
        try
        {
            var allStreams = mediaSourceManager.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
            subtitleStreams = [.. allStreams.Where(stream => stream.Type == MediaStreamType.Subtitle)];
            audioStreams = [.. allStreams.Where(stream => stream.Type == MediaStreamType.Audio)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subsync sync: failed to read media streams for {Item}, aborting", item.Name);
            return Problem("Could not read this item's media streams.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // ISO / BDMV / VIDEO_TS: no single elementary video file for
        // ffsubsync to align against. Read from metadata, not a stat.
        var isDiscImageOrFolder = item is Video video && video.VideoType != VideoType.VideoFile;

        var work = SubtitleWorkBuilder.BuildWork(item.Path, isDiscImageOrFolder, subtitleStreams, audioStreams, config);
        foreach (var subtitle in work.SubtitlesInOtherDirectories)
        {
            logger.LogWarning(
                "Subsync sync: {Subtitle} is not in the same folder as {Video}; the sidecar syncs one folder at a time, skipping",
                subtitle,
                item.Path);
        }

        switch (work.Reason)
        {
            case ItemSkipReason.PathIsDiscImageOrFolder:
                logger.LogWarning(
                    "Subsync sync: {Path} is a disc image or disc folder with no single video file to align against, skipping its subtitles",
                    item.Path);
                break;
            case ItemSkipReason.NoPath:
                logger.LogDebug("Subsync sync: item {Id} has no file path, skipping", item.Id);
                break;
        }

        return request?.SubtitleIndex is { } subtitleIndex
            ? await SyncOneAsync(
                    work, subtitleStreams, audioStreams, config, subtitleIndex,
                    request.ReferenceSubtitleIndex, request.ReferenceEmbeddedSubtitleIndex,
                    request.ReferenceEmbeddedAudioIndex, cancellationToken)
                .ConfigureAwait(false)
            : await SyncAllAsync(item, work, subtitleStreams, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets the UI build a subtitle/reference picker for one item without
    /// triggering a sync - read-only, so unlike <see cref="SyncItem"/> it
    /// doesn't check <see cref="IsSweepRunning"/> or the sidecar's health;
    /// it's safe to call at any time, including mid-sweep.
    /// </summary>
    [HttpGet("{itemId:guid}/Subtitles")]
    public ActionResult<object> GetSubtitles(Guid itemId)
    {
        var item = libraryManager.GetItemById(itemId);
        if (item is null)
            return NotFound();

        var config = configurationProvider.GetSnapshot();

        IReadOnlyList<MediaStream> subtitleStreams;
        IReadOnlyList<MediaStream> audioStreams;
        try
        {
            var allStreams = mediaSourceManager.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
            subtitleStreams = [.. allStreams.Where(stream => stream.Type == MediaStreamType.Subtitle)];
            audioStreams = [.. allStreams.Where(stream => stream.Type == MediaStreamType.Audio)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subsync sync: failed to read media streams for {Item}, aborting", item.Name);
            return Problem("Could not read this item's media streams.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var isDiscImageOrFolder = item is Video video && video.VideoType != VideoType.VideoFile;
        var work = SubtitleWorkBuilder.BuildWork(item.Path, isDiscImageOrFolder, subtitleStreams, audioStreams, config);
        var candidates = work.Group is null
            ? []
            : SubtitleWorkBuilder.BuildCandidateList(
                work.Group, subtitleStreams, skipCache.IsCached, failCache.HasPriorFailure);
        var embeddedSubtitles = SubtitleWorkBuilder.BuildEmbeddedSubtitleCandidates(subtitleStreams, config);
        var embeddedAudio = SubtitleWorkBuilder.BuildEmbeddedAudioCandidates(audioStreams);

        // Projected to lowercase-first keys, matching every other response
        // this controller hands back - the domain records' own PascalCase
        // properties would otherwise reach the client as-is (Jellyfin's JSON
        // pipeline preserves declared casing, it doesn't camelCase it), and
        // the frontend picker reads lowercase keys.
        return Ok(new
        {
            reason = work.Reason.ToString(),
            subtitles = candidates.Select(c => new
            {
                index = c.Index,
                path = c.Path,
                language = c.Language,
                languageName = LanguageName(c.Language),
                title = c.Title,
                isForced = c.IsForced,
                isAlreadySynced = c.IsAlreadySynced,
                hasFailed = c.HasFailed
            }),
            embeddedSubtitles = embeddedSubtitles.Select(c => new
            {
                index = c.Index,
                language = c.Language,
                languageName = LanguageName(c.Language),
                title = c.Title,
                isPgs = c.IsPgs
            }),
            embeddedAudio = embeddedAudio.Select(c => new
            {
                index = c.Index,
                language = c.Language,
                languageName = LanguageName(c.Language),
                title = c.Title,
                codec = c.Codec,
                channels = c.Channels,
                channelLayout = c.ChannelLayout
            })
        });

        // Jellyfin's own curated culture list, not the browser's -
        // MediaStream.Language is an ISO 639 code ("eng"), which isn't
        // something an admin should have to decode. Null when the stream
        // has no language or Jellyfin doesn't recognize the code; the
        // frontend falls back to the raw code then.
        string? LanguageName(string? language)
            => string.IsNullOrEmpty(language) ? null : localizationManager.FindLanguageInfo(language)?.DisplayName;
    }

    /// <summary>
    /// Today's whole-item behavior: clears the skip/fail cache for every
    /// external subtitle Jellyfin knows about for this item (not just the
    /// eligible ones - see <see cref="SubtitleMatcher.GetExternalSubtitlePaths(System.Collections.Generic.IEnumerable{MediaStream})"/>),
    /// then syncs each eligible one in order so the first syncs against the
    /// video and the rest can find it as an already-synced sibling.
    /// </summary>
    private async Task<ActionResult<object>> SyncAllAsync(
        BaseItem item,
        ItemSubtitleWork work,
        IReadOnlyList<MediaStream> subtitleStreams,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var externalSubtitlePaths = SubtitleMatcher.GetExternalSubtitlePaths(subtitleStreams).ToList();
        var removed = skipCache.RemoveForPaths(externalSubtitlePaths);
        var removedFailures = failCache.RemoveForPaths(externalSubtitlePaths);
        logger.LogInformation(
            "Subsync cache: cleared {Count} skip-cache and {FailureCount} fail-cache entr(ies) for {Item}",
            removed, removedFailures, item.Name);

        if (work.Group is null)
            return Ok(new
            {
                cleared = removed + removedFailures, reason = work.Reason.ToString(), results = Array.Empty<object>()
            });

        var orchestrator = new SubtitleSyncOrchestrator(client, skipCache, failCache, logger, suppressor);
        var results = new List<object>();

        try
        {
            // Sequential, not parallel: mirrors the sweep task's own per-group
            // ordering so the first subtitle syncs against the video and later
            // ones can sync against it as an already-synced sibling instead.
            foreach (var subtitlePath in work.Group.SubtitlePaths)
            {
                try
                {
                    var outcome = await orchestrator
                        .ProcessAsync(config, work.Group, subtitlePath, cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(new { path = subtitlePath, outcome = outcome?.ToString() ?? "Skipped" });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Subsync sync: failed to process {Subtitle}, continuing", subtitlePath);
                    results.Add(new { path = subtitlePath, outcome = "Error" });
                }
            }
        }
        finally
        {
            // A request-scoped one-off write, not a long sweep - this is the
            // only point that guarantees any marks from the loop above
            // (including a partially-completed one) reach disk.
            skipCache.Flush();
            failCache.Flush();
        }

        return Ok(new { cleared = removed + removedFailures, reason = work.Reason.ToString(), results });
    }

    /// <summary>
    /// Syncs exactly one of an item's eligible subtitles against an
    /// explicitly chosen reference (another eligible subtitle, a specific
    /// embedded subtitle/audio stream, or the video's own auto-classification
    /// by default) instead of every subtitle Jellyfin knows about for the
    /// item. Unlike <see cref="SyncAllAsync"/>, only the one subtitle being
    /// (re)synced has its skip-cache entry cleared first - clearing every
    /// sibling's too would undo exactly the "without affecting others"
    /// behavior this endpoint exists for, and would also wipe the cache
    /// state that makes a sibling eligible to be picked as a known-good
    /// reference. The fail-cache's consecutive-failure cap is bypassed
    /// outright for this one attempt (see <see cref="SubtitleSyncOrchestrator.ProcessAsync"/>'s
    /// <c>bypassFailureCap</c>) rather than cleared beforehand - a
    /// deliberate, one-off admin retry doesn't need the sweep's protection
    /// against repeatedly re-attempting a dead-end file, but the fail-cache
    /// itself still updates exactly as normal either way: cleared on a
    /// successful outcome, extended on another failure.
    /// </summary>
    private async Task<ActionResult<object>> SyncOneAsync(
        ItemSubtitleWork work,
        IReadOnlyList<MediaStream> subtitleStreams,
        IReadOnlyList<MediaStream> audioStreams,
        PluginConfiguration config,
        int subtitleIndex,
        int? referenceSubtitleIndex,
        int? referenceEmbeddedSubtitleIndex,
        int? referenceEmbeddedAudioIndex,
        CancellationToken cancellationToken)
    {
        if (work.Group is null)
            return Ok(new { cleared = 0, reason = work.Reason.ToString(), results = Array.Empty<object>() });

        var referenceFieldsGiven = new[]
                { referenceSubtitleIndex, referenceEmbeddedSubtitleIndex, referenceEmbeddedAudioIndex }
            .Count(index => index is not null);
        if (referenceFieldsGiven > 1)
            return BadRequest(new
            {
                error = "Only one of referenceSubtitleIndex, referenceEmbeddedSubtitleIndex or "
                        + "referenceEmbeddedAudioIndex can be given."
            });

        var candidates = SubtitleWorkBuilder.BuildCandidateList(
            work.Group, subtitleStreams, skipCache.IsCached, failCache.HasPriorFailure);

        var target = candidates.FirstOrDefault(c => c.Index == subtitleIndex);
        if (target is null)
            return BadRequest(new
            {
                error = $"Subtitle index {subtitleIndex} is not an eligible external subtitle for this item."
            });

        string referencePath;
        JellyfinReportedSituation? situationOverride = null;
        int? referenceStreamIndexOverride = null;

        if (referenceSubtitleIndex is { } referenceIndex)
        {
            if (referenceIndex == subtitleIndex)
                return BadRequest(new
                {
                    error = "The reference subtitle can't be the same as the subtitle being synced."
                });

            var reference = candidates.FirstOrDefault(c => c.Index == referenceIndex);
            if (reference is null)
                return BadRequest(new
                {
                    error =
                        $"Reference subtitle index {referenceIndex} is not an eligible external subtitle for this item."
                });

            referencePath = reference.Path;
        }
        else if (referenceEmbeddedSubtitleIndex is { } embeddedSubtitleIndex)
        {
            var embeddedSubtitle = SubtitleWorkBuilder
                .BuildEmbeddedSubtitleCandidates(subtitleStreams, config)
                .FirstOrDefault(c => c.Index == embeddedSubtitleIndex);
            if (embeddedSubtitle is null)
                return BadRequest(new
                {
                    error = $"Embedded subtitle stream index {embeddedSubtitleIndex} is not an eligible "
                            + "reference for this item."
                });

            referencePath = work.Group.VideoPath;
            situationOverride = embeddedSubtitle.IsPgs
                ? JellyfinReportedSituation.HasFullPgsEmbeddedSubtitles
                : JellyfinReportedSituation.HasFullEmbeddedSubtitles;
            referenceStreamIndexOverride =
                SubtitleWorkBuilder.RelativeEmbeddedSubtitleIndex(subtitleStreams, embeddedSubtitle.Index);
        }
        else if (referenceEmbeddedAudioIndex is { } embeddedAudioIndex)
        {
            var embeddedAudio = SubtitleWorkBuilder
                .BuildEmbeddedAudioCandidates(audioStreams)
                .FirstOrDefault(c => c.Index == embeddedAudioIndex);
            if (embeddedAudio is null)
                return BadRequest(new
                {
                    error = $"Embedded audio stream index {embeddedAudioIndex} is not an eligible reference "
                            + "for this item."
                });

            referencePath = work.Group.VideoPath;
            situationOverride = JellyfinReportedSituation.ManuallyTargetedAudio;
            referenceStreamIndexOverride =
                SubtitleWorkBuilder.RelativeEmbeddedAudioIndex(audioStreams, embeddedAudio.Index);
        }
        else
        {
            referencePath = work.Group.VideoPath;
        }

        var removed = skipCache.RemoveForPaths([target.Path]);
        logger.LogInformation(
            "Subsync cache: cleared {Count} skip-cache entr(ies) for {Subtitle}",
            removed, target.Path);

        var orchestrator = new SubtitleSyncOrchestrator(client, skipCache, failCache, logger, suppressor);
        object result;

        try
        {
            var outcome = await orchestrator
                .ProcessAsync(
                    config, work.Group, target.Path, cancellationToken,
                    referencePathOverride: referencePath,
                    situationOverride: situationOverride,
                    referenceStreamIndexOverride: referenceStreamIndexOverride,
                    // A deliberate, one-off admin action - unlike the sweep,
                    // it doesn't need protecting from re-attempting a
                    // dead-end file every run, so the "failed too many times
                    // in a row" cap doesn't apply here. The outcome still
                    // updates the fail-cache as normal either way.
                    bypassFailureCap: true)
                .ConfigureAwait(false);
            result = new { path = target.Path, outcome = outcome?.ToString() ?? "Skipped" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subsync sync: failed to process {Subtitle}, continuing", target.Path);
            result = new { path = target.Path, outcome = "Error" };
        }
        finally
        {
            skipCache.Flush();
            failCache.Flush();
        }

        return Ok(new
            { cleared = removed, reason = work.Reason.ToString(), results = new[] { result } });
    }

    private bool IsSweepRunning() =>
        taskManager.ScheduledTasks.Any(worker =>
            worker is { ScheduledTask: SyncLibrarySweepTask, State: TaskState.Running or TaskState.Cancelling });

    /// <summary>
    /// Optional body for <see cref="SyncItem"/>. Absent (or an absent/null
    /// <see cref="SubtitleIndex"/>) means "sync every eligible subtitle",
    /// today's only behavior. A <see cref="SubtitleIndex"/> narrows the
    /// request to that one subtitle; at most one of
    /// <see cref="ReferenceSubtitleIndex"/>, <see cref="ReferenceEmbeddedSubtitleIndex"/>
    /// or <see cref="ReferenceEmbeddedAudioIndex"/> then optionally names
    /// what to align it against - another eligible external subtitle, a
    /// specific embedded subtitle stream, or a specific embedded audio
    /// stream, respectively - instead of the video (letting
    /// <see cref="Infrastructure.SubtitleWorkBuilder.BuildWork"/>'s own
    /// auto-classification decide), which is the default when all three are
    /// absent. Every index is a raw <c>MediaStream.Index</c> value, matched
    /// against the same candidate lists <c>GET .../Subtitles</c> returns, so
    /// a client never has to round-trip a filesystem path. Can't be
    /// narrower than public: it's part of <see cref="SyncItem"/>'s own
    /// signature, and a public method can't expose a less-accessible type -
    /// nesting it here is as narrow as C# allows while still binding it
    /// directly as that action's <c>[FromBody]</c> parameter type.
    /// </summary>
    public sealed record SyncItemRequest(
        int? SubtitleIndex,
        int? ReferenceSubtitleIndex,
        int? ReferenceEmbeddedSubtitleIndex,
        int? ReferenceEmbeddedAudioIndex);
}