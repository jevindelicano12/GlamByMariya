using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlamByMariya.Infrastructure.Integrations;

public sealed record XenditInvoice(string Id, string InvoiceUrl);

// Xendit invoices for the booking down payment (test mode for the demo).
// Settings: Xendit:SecretKey (xnd_development_...), Xendit:CallbackToken (webhook
// verification token), optional Xendit:BaseUrl. Live keys are refused unless
// Xendit:AllowLiveKeys=true is set on purpose.
public sealed class XenditClient(IHttpClientFactory http, IConfiguration config)
{
    // One minute shorter than the 15-minute hold, so a payment always lands while the slot is still held.
    public const int InvoiceSeconds = 14 * 60;
    public const int MinimumInvoiceSeconds = 2 * 60;
    // The checkout offers only e-wallets (GCash, Maya, GrabPay, ShopeePay) and QR Ph; no cards, banks or outlets.
    public static readonly string[] PaymentMethods = ["GCASH", "PAYMAYA", "GRABPAY", "SHOPEEPAY", "QRPH"];

    // A hold that started earlier (an approved early request): the page closes one minute before the
    // hold ends, or is not opened at all (null) when less than two minutes would remain.
    public static int? SecondsBefore(DateTime paymentDue, DateTime now)
    {
        var seconds = (int)Math.Floor((paymentDue - now).TotalSeconds) - 60;
        return seconds < MinimumInvoiceSeconds ? null : Math.Min(seconds, InvoiceSeconds);
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(config["Xendit:SecretKey"]) && !string.IsNullOrWhiteSpace(config["Xendit:CallbackToken"]);
    // True only with a Xendit TEST key (xnd_development_...): every payment is test money.
    public bool IsTestMode => Enabled && (config["Xendit:SecretKey"] ?? "").StartsWith("xnd_development_", StringComparison.Ordinal);

    public async Task<XenditInvoice> CreateInvoiceAsync(string externalId, decimal amount, string payerEmail, string description,
        string successUrl, string failureUrl, CancellationToken ct = default, int? durationSeconds = null)
    {
        var key = config["Xendit:SecretKey"] ?? throw new InvalidOperationException("Xendit is not configured.");
        if (!key.StartsWith("xnd_development_", StringComparison.Ordinal) && !config.GetValue("Xendit:AllowLiveKeys", false))
            throw new InvalidOperationException("Use a Xendit test key (xnd_development_...) for this demo.");
        var baseUrl = new Uri((config["Xendit:BaseUrl"] ?? "https://api.xendit.co").TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "v2/invoices"))
        {
            Content = JsonContent.Create(new
            {
                external_id = externalId,
                amount,
                currency = "PHP",
                payer_email = payerEmail,
                description,
                invoice_duration = durationSeconds ?? InvoiceSeconds,
                success_redirect_url = successUrl,
                failure_redirect_url = failureUrl,
                payment_methods = PaymentMethods,
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":")));
        using var client = http.CreateClient("xendit");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Xendit did not create the invoice ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = json.RootElement.GetProperty("id").GetString();
        var url = json.RootElement.GetProperty("invoice_url").GetString();
        if (string.IsNullOrWhiteSpace(id) || !Uri.TryCreate(url, UriKind.Absolute, out var checkout) || checkout.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Xendit returned an invalid invoice.");
        return new(id, checkout.ToString());
    }

    // The webhook's x-callback-token must equal the configured token (constant-time compare).
    public bool CallbackTokenMatches(string? token)
    {
        var expected = config["Xendit:CallbackToken"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }
}
