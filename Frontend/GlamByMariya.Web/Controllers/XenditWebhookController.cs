using System.Text.Json;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlamByMariya.Web.Controllers;

// Xendit invoice callbacks. The only proof the call comes from Xendit is the
// x-callback-token header; the database then checks invoice, amount and booking
// state, and ignores a repeated event. Only this webhook confirms a Xendit booking.
[AllowAnonymous, IgnoreAntiforgeryToken]
public class XenditWebhookController(IStudioStore store, XenditClient xendit, ILogger<XenditWebhookController> log) : Controller
{
    [HttpPost("/payments/xendit/webhook"), RequestSizeLimit(65_536)]
    public async Task<IActionResult> Receive()
    {
        if (!xendit.Enabled || store is not IPersistentStudioStore database) return NotFound();
        if (!xendit.CallbackTokenMatches(Request.Headers["x-callback-token"].FirstOrDefault())) return Unauthorized();
        JsonDocument json;
        try { json = await JsonDocument.ParseAsync(Request.Body); }
        catch (JsonException) { return BadRequest(); }
        using (json)
        {
            var (status, result) = Apply(database, json.RootElement);
            if (status != 200) log.LogWarning("Xendit webhook refused: {Result}", result);
            return StatusCode(status, new { result });
        }
    }

    // Applies one invoice callback body. Returns the HTTP status and a short result.
    public static (int Status, string Result) Apply(IPersistentStudioStore database, JsonElement body)
    {
        string? Text(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal? Number(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
        var invoice = Text("id");
        var status = Text("status");
        if (string.IsNullOrWhiteSpace(invoice) || string.IsNullOrWhiteSpace(status)) return (400, "missing invoice id or status");
        if ((Text("currency") ?? "PHP") != "PHP") return (400, "currency must be PHP");
        var eventKey = $"{invoice}:{status}";
        try
        {
            return status switch
            {
                "PAID" or "SETTLED" when (Number("paid_amount") ?? Number("amount")) is { } amount => (200, database.XenditPaid(invoice, amount, eventKey)),
                "PAID" or "SETTLED" => (400, "missing paid amount"),
                "EXPIRED" => (200, database.XenditExpired(invoice, eventKey)),
                _ => (200, "ignored"),
            };
        }
        catch (InvalidOperationException e) { return (400, e.Message); }
    }
}
