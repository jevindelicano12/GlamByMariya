using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;

namespace GlamByMariya.Web.Controllers;

public sealed class WebsiteImagesController : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Image(Guid id, CancellationToken cancellationToken)
    {
        var demo = HttpContext.RequestServices.GetService<DemoGalleryStore>();
        if (demo != null)
        {
            (byte[] Bytes, string Type)? photo;
            try { photo = (HttpContext.RequestServices.GetService<IWebsiteContentRepository>() as DemoFileWebsiteContentRepository)?.ReadImage(id, false) ?? demo.Read(id); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { return NotFound(); }
            if (photo == null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(photo.Value.Bytes, photo.Value.Type);
        }
        var service = HttpContext.RequestServices.GetService<WebsitePublicImageService>();
        if (service == null) return NotFound();
        try
        {
            var image = await service.Read(id, cancellationToken);
            if (image == null) return NotFound();
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(image.Bytes, image.ContentType);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return NotFound();
        }
    }
}
