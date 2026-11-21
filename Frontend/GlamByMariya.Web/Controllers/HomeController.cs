using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
namespace GlamByMariya.Web.Controllers;
    public class HomeController(IStudioStore store, CatalogImageService imageService, ILogger<HomeController> logger) : Controller
{
    private async Task LoadPublished(CancellationToken ct)
    {
        var content = HttpContext.RequestServices.GetService<WebsiteContentService>();
        if (content != null)
        {
            try { ViewData["PublishedWebsiteContent"] = (await content.PublicContent(ct))?.Text; }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning("Published website content could not be loaded; using placeholders."); }
        }
        var images = HttpContext.RequestServices.GetService<WebsiteImageService>();
        if (images != null && HttpContext.RequestServices.GetService<WebsitePublicImageService>() != null)
        {
            try { ViewData["PublishedWebsiteImages"] = await images.PublishedImages(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning("Published website images could not be loaded; using placeholders."); }
        }
        var demoGallery = HttpContext.RequestServices.GetService<DemoGalleryStore>();
        if (demoGallery != null)
        {
            var published = demoGallery.List().Where(x => x.Visible)
                .Select(x => new WebsiteImageReference(x.Id, x.Id, "gallery", x.AltText, x.Caption, 0, true)).ToList();
            try { if (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() is DemoFileWebsiteContentRepository demo) published.AddRange(demo.Images(false)); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { logger.LogWarning("Published demo website images could not be loaded."); }
            ViewData["PublishedWebsiteImages"] = published;
        }
    }
    public async Task<IActionResult> Index(CancellationToken ct) { await LoadPublished(ct); ViewBag.CatalogImages = (Func<string, Guid, string?>)((kind, id) => imageService.GetImageUrl(kind, id)); lock (store.Gate) return View(store.Services.Where(x => x.Active && !x.Archived).ToList()); }
    public async Task<IActionResult> About(CancellationToken ct) { await LoadPublished(ct); return View(); }
    public IActionResult Services() { lock (store.Gate) return View(store.Services.Where(x => x.Active && !x.Archived).ToList()); }
    public async Task<IActionResult> Gallery(CancellationToken ct) { await LoadPublished(ct); return View(); }
    public async Task<IActionResult> Contact(CancellationToken ct) { await LoadPublished(ct); return View(); }
    public IActionResult Terms() => View();
    public IActionResult Error() => Problem("An unexpected error occurred. Please try again.");
}
