using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

using OpenVideoMenu;

namespace Jellyfin.Plugin.OpenVideoMenu;

/// <summary>
/// The Open Video Menu asset API (v1), served by Jellyfin itself at /OpenVideoMenu/v1/...: the same endpoints
/// and shapes as the standalone asset server, so a TV app points its asset client at
/// {jellyfin}/OpenVideoMenu and changes nothing else. Sign-in is Jellyfin's own: any signed-in user of
/// this server may read its menus, as they may play its media.
/// </summary>
[ApiController]
[Route("OpenVideoMenu/v1")]
public class OpenVideoMenuController(MenuCatalog catalog, Microsoft.Extensions.Logging.ILogger<OpenVideoMenuController> log) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Unauthenticated: tells apps this server serves menus natively.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public IActionResult Health() => Ok(new
    {
        ok = true,
        name = "Open Video Menu for Jellyfin",
        api = "1",
        format = Format.Version,
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
        var found = await catalog.ResolveFile(root, Uri.UnescapeDataString(path), cancel).ConfigureAwait(false);
        // 404 for anything not allowlisted, so the server doesn't confirm which other files exist.
        if (found is null || !System.IO.File.Exists(found.File))
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(log, "Open Video Menu: refused files/{Root}/{Path}", root, path);
            return NotFound();
        }

        var type = found.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json; charset=utf-8"
            : ContentTypes.TryGetContentType(found.Name, out var t) ? t : "application/octet-stream";
        Response.Headers.CacheControl = "private, max-age=60";
        if (found.Length is { } length)
        {
            // A bundle member: a seekable window onto the bundle file, so range requests work as for a file.
            var stream = new FileStream(found.File, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return File(new SliceStream(stream, found.Start, length), type, enableRangeProcessing: true);
        }

        return PhysicalFile(found.File, type, enableRangeProcessing: true);
    }
}
