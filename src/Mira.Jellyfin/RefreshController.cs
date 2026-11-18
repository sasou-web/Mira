using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mira.Jellyfin;

/// <summary>
/// « Actualiser les métadonnées » and « Rechercher les métadonnées manquantes » of Mira's apps, for Jellyfin's
/// administrators only: Jellyfin's <c>Items/{id}/Refresh</c> in FullRefresh, without reading every extra of the title
/// again at once (<see cref="TitleRefresh"/>). Queued: answers at once, as Jellyfin does.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Mira/refresh")]
public sealed class RefreshController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly TitleRefresh _refresh;

    public RefreshController(ILibraryManager library, IFileSystem fileSystem, TitleRefresh refresh)
    {
        _library = library; _fileSystem = fileSystem; _refresh = refresh;
    }

    /// <param name="id">The title.</param>
    /// <param name="images">Its missing images looked for too (« Actualiser les métadonnées »), not only its text.</param>
    [HttpPost("{id}")]
    public IActionResult Refresh([FromRoute] Guid id, [FromQuery] bool images = false)
    {
        if (_library.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        _refresh.Queue(item, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = images ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            ForceSave = true,
            IsAutomated = false,
        });
        return NoContent();
    }
}
