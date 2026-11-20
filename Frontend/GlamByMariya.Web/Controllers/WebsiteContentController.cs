using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using System.Security.Claims;

namespace GlamByMariya.Web.Controllers;

[Authorize(Roles = "SuperAdmin")]
public sealed class WebsiteContentController : Controller
{
    // Normal startup leaves the service unregistered; local Demo can opt in.
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return View(new WebsiteContentEditorModel());
        ViewData["DemoTextEditing"] = HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoWebsiteContentRepository;
        ViewData["DemoPersistentText"] = HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository;
        try
        {
            if (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository local) ViewData["DemoDraftImages"] = local.Images(true);
            var draft = await service.Preview(User, cancellationToken);
            if (draft != null) await LoadDraftImages(draft.Id, cancellationToken);
            return View(WebsiteContentEditorModel.FromDraft(draft));
        }
        catch (Exception e) when (e is not OperationCanceledException) { return StatusCode(503, "Website content is temporarily unavailable."); }
    }

    // Live photo cards appear only when uploads are registered (database mode).
    private async Task LoadDraftImages(Guid draftId, CancellationToken cancellationToken)
    {
        if (HttpContext.RequestServices.GetService<WebsiteImageUploadService>() == null || HttpContext.RequestServices.GetService<WebsiteImageService>() is not { } images) return;
        ViewData["DraftImages"] = await images.DraftImages(User, draftId, cancellationToken);
    }

    private const string PhotoStorageDown = "Photos can't be saved right now. Text changes still work.";

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(5_100_000)]
    public async Task<IActionResult> SaveImage(Guid draftId, long revision, string role, string? altText, string? caption, IFormFile? image, CancellationToken cancellationToken)
    {
        var uploads = HttpContext.RequestServices.GetService<WebsiteImageUploadService>();
        if (uploads == null) return NotFound();
        if (image == null || image.Length <= 0 || image.Length > WebsiteContentRules.MaxImageBytes) { TempData["Error"] = "Choose a JPG or PNG image up to 5 MB."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var buffer = new MemoryStream(); await image.CopyToAsync(buffer, cancellationToken);
            await uploads.Save(User, draftId, revision, role, altText, caption, buffer.ToArray(), cancellationToken);
            TempData["Success"] = "Photo saved to the draft. Publish the draft to make it public.";
        }
        catch (ArgumentException e) { TempData["Error"] = e.Message; }
        catch (WebsiteImageStorageException) { TempData["Error"] = PhotoStorageDown; }
        catch (InvalidOperationException) { TempData["Error"] = "The draft changed. Reload and try again."; }
        catch (Exception e) when (e is not OperationCanceledException and not UnauthorizedAccessException) { TempData["Error"] = PhotoStorageDown; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveImage(Guid draftId, long revision, Guid imageId, CancellationToken cancellationToken)
    {
        var uploads = HttpContext.RequestServices.GetService<WebsiteImageUploadService>();
        if (uploads == null) return NotFound();
        try
        {
            await uploads.Remove(User, draftId, revision, imageId, cancellationToken);
            TempData["Success"] = "Photo removed from the draft. Publish the draft to update the website.";
        }
        catch (ArgumentException e) { TempData["Error"] = e.Message; }
        catch (InvalidOperationException) { TempData["Error"] = "The draft changed. Reload and try again."; }
        catch (Exception e) when (e is not OperationCanceledException and not UnauthorizedAccessException) { TempData["Error"] = "The photo could not be removed. Please try again."; }
        return RedirectToAction(nameof(Index));
    }

    private WebsiteContentService? ContentService => HttpContext.RequestServices.GetService<WebsiteContentService>();
    private void RecordDemoPublication(Guid draftId)
    {
        if (HttpContext.RequestServices.GetService<IStudioStore>() is DemoStore demo && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
            demo.RecordAdminActivity(actor, "Website content published", "Website Content", draftId);
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DraftImage(Guid id, CancellationToken cancellationToken)
    {
        if (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository local)
        {
            try { var image = local.ReadImage(id, true); if (image == null) return NotFound(); Response.Headers.XContentTypeOptions = "nosniff"; return File(image.Value.Bytes, image.Value.Type); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { return NotFound(); }
        }
        var service = HttpContext.RequestServices.GetService<WebsiteDraftImageService>();
        if (service == null) return NotFound();
        try
        {
            var image = await service.Read(User, id, cancellationToken);
            if (image == null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(image.Bytes, image.ContentType);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return NotFound(); }
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(5_100_000)]
    public async Task<IActionResult> SaveDemoImage(Guid draftId, long revision, string role, string? altText, IFormFile? image, CancellationToken cancellationToken)
    {
        if (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is not DemoFileWebsiteContentRepository local) return NotFound();
        if (image == null || image.Length <= 0 || image.Length > WebsiteContentRules.MaxImageBytes) { TempData["Error"] = "Choose a JPG or PNG image up to 5 MB."; return RedirectToAction(nameof(Index)); }
        try
        {
            await using var buffer = new MemoryStream(); await image.CopyToAsync(buffer, cancellationToken);
            local.SaveImage(draftId, revision, role, altText, buffer.ToArray());
            TempData["Success"] = "Image saved to the draft. Publish the draft to make it public.";
            return RedirectToAction(nameof(Index));
        }
        catch (ArgumentException e) { TempData["Error"] = e.Message; return RedirectToAction(nameof(Index)); }
        catch (InvalidOperationException) { TempData["Error"] = "The image is invalid or the draft changed. Reload and try again."; return RedirectToAction(nameof(Index)); }
        catch (IOException) { return StatusCode(503, "Image save could not be confirmed. Reload before retrying."); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDraftForm(CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website editing is not available.");
        try { await service.CreateDraft(User, cancellationToken); return RedirectToAction(nameof(Index)); }
        catch (Exception e) when (e is not OperationCanceledException) { return StatusCode(503, "The draft could not be created. Please try again."); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveForm(WebsiteContentEditorModel input, CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website editing is not available.");
        ViewData["DemoTextEditing"] = HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoWebsiteContentRepository;
        ViewData["DemoPersistentText"] = HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository;
        input.Enabled = true; input.HasDraft = true;
        try
        {
            if (!ModelState.IsValid) throw new ArgumentException("Check the submitted fields.");
            await service.Save(User, input.DraftId, input.Revision, input.ToText(), cancellationToken);
            return RedirectToAction(nameof(Index));
        }
        catch (ArgumentException e) { ModelState.AddModelError("", e.Message); }
        catch (InvalidOperationException) { ModelState.AddModelError("", "The draft changed or could not be saved. Copy your changes before reloading."); }
        catch (Exception e) when (e is not OperationCanceledException) { ModelState.AddModelError("", "Saving failed. Your submitted text remains below; try again."); }
        input.Links ??= []; input.Faqs ??= [];
        input.AddEmptySlots();
        try { await LoadDraftImages(input.DraftId, cancellationToken); } catch (Exception e) when (e is not OperationCanceledException) { }
        return View("Index", input);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult DiscardDemoDraft(Guid draftId, long revision)
    {
        var store = HttpContext.RequestServices.GetService<IWebsiteContentRepository>() as DemoFileWebsiteContentRepository;
        if (store == null) return NotFound();
        try { store.Discard(draftId, revision); return RedirectToAction(nameof(Index)); }
        catch (InvalidOperationException) { return Conflict("The draft changed. Reload before discarding."); }
        catch (IOException) { return StatusCode(503, "Discard could not be confirmed. Reload before retrying."); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PublishForm(Guid draftId, long revision, CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website publishing is not available.");
        try { await service.Publish(User, draftId, revision, cancellationToken); RecordDemoPublication(draftId); return RedirectToAction(nameof(Index)); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException) { return Conflict("The draft changed or could not be published. Reload and try again."); }
        catch (Exception e) when (e is not OperationCanceledException) { return StatusCode(503, "Publishing could not be confirmed. Check published content before retrying."); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDraft(CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website editing is not available.");
        try { return Ok(await service.CreateDraft(User, cancellationToken)); }
        catch (InvalidOperationException) { return Conflict("The draft could not be created. Please try again."); }
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Preview(CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website preview is not available.");
        var draft = await service.Preview(User, cancellationToken);
        if (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository local) ViewData["DemoDraftImages"] = local.Images(true);
        return draft == null ? NotFound() : View(draft);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid draftId, long revision, [FromBody] WebsiteTextInput? text, CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website editing is not available.");
        if (!ModelState.IsValid || text == null) return BadRequest("Provide valid website content.");
        try { return Ok(await service.Save(User, draftId, revision, text, cancellationToken)); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException) { return Conflict("The draft could not be saved. Reload before retrying."); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(Guid draftId, long revision, CancellationToken cancellationToken)
    {
        var service = ContentService;
        if (service == null) return StatusCode(503, "Website publishing is not available.");
        try { var published = await service.Publish(User, draftId, revision, cancellationToken); RecordDemoPublication(draftId); return Ok(published); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException) { return Conflict("The draft could not be published. Reload before retrying."); }
    }
}
