using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlamByMariya.Web.Controllers;

[Authorize(Roles = "Customer,Admin,SuperAdmin")]
public class PaymentsController(PaymentQrService paymentQr) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Qr(string method)
    {
        try
        {
            var image = paymentQr.Read(method);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return image == null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
        }
        catch (InvalidOperationException) { return BadRequest(); }
    }
}
