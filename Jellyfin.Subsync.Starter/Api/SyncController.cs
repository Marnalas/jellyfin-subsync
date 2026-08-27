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
    ILogger<SyncController> logger) : ControllerBase
{
    [HttpPost("Items/{itemId:guid}")]
    public async Task<ActionResult<object>> SyncItem(Guid itemId, CancellationToken cancellationToken)
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
        try
        {
            subtitleStreams = mediaSourceManager.GetMediaStreams(new MediaStreamQuery
            {
                ItemId = item.Id,
                Type = MediaStreamType.Subtitle
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subsync sync: failed to read media streams for {Item}, aborting", item.Name);
            return Problem("Could not read this item's media streams.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var externalSubtitlePaths = SubtitleMatcher.GetExternalSubtitlePaths(subtitleStreams).ToList();
        var removed = skipCache.RemoveForPaths(externalSubtitlePaths);
        var removedFailures = failCache.RemoveForPaths(externalSubtitlePaths);
        logger.LogInformation(
            "Subsync cache: cleared {Count} skip-cache and {FailureCount} fail-cache entr(ies) for {Item}",
            removed, removedFailures, item.Name);

        // ISO / BDMV / VIDEO_TS: no single elementary video file for
        // ffsubsync to align against. Read from metadata, not a stat.
        var isDiscImageOrFolder = item is Video video && video.VideoType != VideoType.VideoFile;

        var work = SubtitleWorkBuilder.BuildWork(item.Path, isDiscImageOrFolder, subtitleStreams, config);
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

        if (work.Group is null)
            return Ok(new { cleared = removed + removedFailures, reason = work.Reason.ToString(), results = Array.Empty<object>() });

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

    private bool IsSweepRunning() =>
        taskManager.ScheduledTasks.Any(worker =>
            worker is { ScheduledTask: SyncLibrarySweepTask, State: TaskState.Running or TaskState.Cancelling });
}