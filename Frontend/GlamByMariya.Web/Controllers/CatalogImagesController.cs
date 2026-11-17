using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlamByMariya.Web.Controllers;

[AllowAnonymous]
public class CatalogImagesController(CatalogImageService catalogImages) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Image(string kind, string id, string? slot = null)
    {
        try
        {
            if ((kind != "service" && kind != "addon") || !Guid.TryParse(id, out var guid) || guid == Guid.Empty)
                return BadRequest();
            var resolvedSlot = string.IsNullOrEmpty(slot) ? "card" : slot;
            if (resolvedSlot != "card" && resolvedSlot != "preview")
                return BadRequest();
            var image = catalogImages.Read(kind, guid, resolvedSlot);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return image == null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
        }
        catch (InvalidOperationException) { return BadRequest(); }
    }
}
