using System.Net.Mime;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Api;

/// <summary>
/// Lets an admin erase skip-cache entries from the "Cache" dashboard tab -
/// either everything, or just what's tracked for one library item.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Subsync/SkipCache")]
public class SkipCacheController(
    ISkipCache skipCache,
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    ILogger<SkipCacheController> logger) : ControllerBase
{
    [HttpDelete]
    public ActionResult<object> ClearAll()
    {
        var removed = skipCache.Clear();
        logger.LogInformation("Subsync cache: cleared {Count} skip-cache entr(ies)", removed);
        return Ok(new { removed });
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

        var paths = SubtitleMatcher.GetExternalSubtitlePaths(item, mediaSourceManager);
        var removed = skipCache.RemoveForPaths(paths);
        logger.LogInformation("Subsync cache: cleared {Count} skip-cache entr(ies) for {Item}", removed, item.Name);
        return Ok(new { removed });
    }
}