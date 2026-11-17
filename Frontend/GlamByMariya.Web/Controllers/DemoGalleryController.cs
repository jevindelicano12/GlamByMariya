using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GlamByMariya.Infrastructure.Data;
using System.Security.Claims;

namespace GlamByMariya.Web.Controllers;

[Authorize(Roles = "SuperAdmin")]
public sealed class DemoGalleryController : Controller
{
    private DemoGalleryStore? Store => HttpContext.RequestServices.GetService<DemoGalleryStore>();

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Index() => Store is { } store ? View(store.List()) : NotFound();

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(5_100_000)]
    public async Task<IActionResult> Upload(IFormFile? photo, string? altText, string? caption, CancellationToken ct)
    {
        var store = Store;
        if (store == null) return NotFound();
        if (photo == null || photo.Length <= 0 || photo.Length > WebsiteContentRules.MaxImageBytes)
        { TempData["Error"] = "Choose a JPG or PNG image up to 5 MB."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var buffer = new MemoryStream();
            await photo.CopyToAsync(buffer, ct);
            var saved = store.Upload(buffer.ToArray(), altText, caption);
            RecordActivity("Gallery photo uploaded", saved.Id);
            TempData["Success"] = "Photo added to the gallery.";
        }
        catch (InvalidOperationException e) { TempData["Error"] = e.Message; }
        catch (IOException) { TempData["Error"] = "Photo storage failed; check the local storage folder."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Visibility(Guid id, bool visible)
    {
        var store = Store;
        if (store == null) return NotFound();
        try { store.SetVisibility(id, visible); RecordActivity(visible ? "Gallery photo restored" : "Gallery photo archived", id); TempData["Success"] = visible ? "Photo restored to the gallery." : "Photo archived and hidden from the gallery."; }
        catch (InvalidOperationException) { return NotFound(); }
        catch (IOException) { return StatusCode(503, "Photo visibility could not be saved."); }
        return RedirectToAction(nameof(Index));
    }

    private void RecordActivity(string action, Guid id)
    {
        if (HttpContext.RequestServices.GetService<IStudioStore>() is DemoStore demo && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
            demo.RecordAdminActivity(actor, action, "Gallery", id);
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Photo(Guid id)
    {
        var photo = Store?.Read(id, true);
        if (photo == null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(photo.Value.Bytes, photo.Value.Type);
    }
}
