using System.Net.Mime;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Subsync.Starter.Api
{
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
        IMediaSourceManager mediaSourceManager) : ControllerBase
    {
        [HttpDelete]
        public ActionResult<object> ClearAll() => Ok(new { removed = skipCache.Clear() });

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

            var paths = mediaSourceManager
                .GetMediaStreams(new MediaStreamQuery { ItemId = item.Id, Type = MediaStreamType.Subtitle })
                .Where(stream =>
                    stream.IsExternal && stream.IsExternalUrl != true && !string.IsNullOrEmpty(stream.Path))
                .Select(stream => stream.Path!)
                .Distinct();

            return Ok(new { removed = skipCache.RemoveForPaths(paths) });
        }
    }
}
