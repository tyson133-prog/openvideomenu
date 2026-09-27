using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Jellyfin.Plugin.MenuStream;

/// <summary>
/// The MenuStream asset API (v1), served by Jellyfin itself at /MenuStream/v1/...: the same endpoints
/// and shapes as the standalone asset server, so a TV app points its asset client at
/// {jellyfin}/MenuStream and changes nothing else. Sign-in is Jellyfin's own: any signed-in user of
/// this server may read its menus, as they may play its media.
/// </summary>
[ApiController]
[Route("MenuStream/v1")]
public class MenuStreamController(MenuCatalog catalog, Microsoft.Extensions.Logging.ILogger<MenuStreamController> log) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Unauthenticated: tells apps this server serves menus natively.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Health() => Ok(new
    {
        ok = true,
        name = "MenuStream for Jellyfin",
        api = "1",
        format = MenuCatalog.FormatVersion,
        manifests = catalog.Count,
        auth = "jellyfin",
    });

    /// <summary>Every manifest and the media paths it references. ETag, so an unchanged index is a 304.</summary>
    [HttpGet("index")]
    [Authorize]
    public async Task<IActionResult> Index([FromQuery] string? refresh, CancellationToken cancel)
    {
        if (refresh is not null)
        {
            await catalog.RescanAsync(cancel).ConfigureAwait(false);
        }

        var (_, json, etag) = await catalog.Current(cancel).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(json, MediaTypeNames.Application.Json);
    }

    /// <summary>A manifest or one of the menu assets it names (Range requests supported). Nothing else.</summary>
    [HttpGet("files/{root:int}/{**path}")]
    [Authorize]
    public async Task<IActionResult> Files([FromRoute] int root, [FromRoute] string path, CancellationToken cancel)
    {
        var file = await catalog.ResolveFile(root, Uri.UnescapeDataString(path), cancel).ConfigureAwait(false);
        // 404 for anything not allowlisted, so the server doesn't confirm which other files exist.
        if (file is null || !System.IO.File.Exists(file))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(log, "MenuStream: refused files/{Root}/{Path}", root, path);
            return NotFound();
        }

        var type = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json; charset=utf-8"
            : ContentTypes.TryGetContentType(file, out var t) ? t : "application/octet-stream";
        Response.Headers.CacheControl = "private, max-age=60";
        return PhysicalFile(file, type, enableRangeProcessing: true);
    }
}
