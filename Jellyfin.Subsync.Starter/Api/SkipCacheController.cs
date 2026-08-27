using System.Net.Mime;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Api;

/// <summary>
/// Lets an admin erase skip-cache and fail-cache entries from the "Cache"
/// dashboard tab - either everything, or just what's tracked for one
/// library item. Clearing both together means "evaluate this completely
/// fresh": forget that it's already synced, and forget any failure streak.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Subsync/SkipCache")]
public class SkipCacheController(
    ISkipCache skipCache,
    IFailCache failCache,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    ILogger<SkipCacheController> logger) : ControllerBase
{
    [HttpDelete]
    public ActionResult<object> ClearAll()
    {
        var removed = skipCache.Clear();
        var removedFailures = failCache.Clear();
        logger.LogInformation(
            "Subsync cache: cleared {Count} skip-cache and {FailureCount} fail-cache entr(ies)",
            removed, removedFailures);
        return Ok(new { removed, removedFailures });
    }

    /// <summary>
    /// Removes cache entries for every external subtitle Jellyfin currently
    /// associates with this item - not filtered through the plugin's
    /// configured SubtitleExtensions, so a later config change can't leave
    /// stale entries this endpoint can no longer reach.
    /// </summary>
    [HttpDelete("Items/{itemId:guid}")]
    public ActionResult<object> ClearForItem(Guid itemId)
    {
        var item = libraryManager.GetItemById(itemId);
        if (item is null)
            return NotFound();

        var paths = SubtitleMatcher.GetExternalSubtitlePaths(item, mediaSourceManager).ToList();
        var removed = skipCache.RemoveForPaths(paths);
        var removedFailures = failCache.RemoveForPaths(paths);
        logger.LogInformation(
            "Subsync cache: cleared {Count} skip-cache and {FailureCount} fail-cache entr(ies) for {Item}",
            removed, removedFailures, item.Name);
        return Ok(new { removed, removedFailures });
    }
}