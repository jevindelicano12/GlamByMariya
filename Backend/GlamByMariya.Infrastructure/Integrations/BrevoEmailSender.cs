using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GlamByMariya.Infrastructure.Integrations;

/// <summary>Server-only Brevo Transactional Email API client.</summary>
public sealed class BrevoEmailSender(
    IHttpClientFactory clients,
    IConfiguration config,
    ILogger<BrevoEmailSender> log)
{
    private static readonly Uri Endpoint = new("https://api.brevo.com/v3/smtp/email");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    public async Task SendAsync(string recipient, string subject, string text)
    {
        if (config.GetValue("Email:DeliveryDisabled", false))
            throw new InvalidOperationException("Email delivery is disabled for this environment.");
        var apiKey = config["Email:BrevoApiKey"];
        var from = config["Email:From"];

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Transactional email is not configured. Set Email__BrevoApiKey in the hosting environment.");
        if (string.IsNullOrWhiteSpace(from) || !System.Net.Mail.MailAddress.TryCreate(from, out _))
            throw new InvalidOperationException("Email sender is not configured. Set Email__From to the verified sender address.");

        using var client = clients.CreateClient(nameof(BrevoEmailSender));
        client.Timeout = RequestTimeout;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(new BrevoEmailRequest(
            new BrevoSender(from.Trim()),
            [new BrevoRecipient(recipient.Trim())],
            subject,
            text));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                // Do not include Brevo's response body, recipient, or API key in
                // logs or the exception shown by the UI.
                log.LogWarning("Brevo email API returned HTTP {StatusCode}.", (int)response.StatusCode);
                throw new InvalidOperationException("Brevo could not accept the email. Check the API key and verified sender in Render, then try again.");
            }
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Brevo email API request timed out.");
            throw new InvalidOperationException("Email delivery timed out. Please wait briefly and try again.");
        }
        catch (HttpRequestException)
        {
            log.LogWarning("Brevo email API could not be reached.");
            throw new InvalidOperationException("Email delivery is temporarily unavailable. Please try again shortly.");
        }
    }

    private sealed record BrevoEmailRequest(
        [property: JsonPropertyName("sender")] BrevoSender Sender,
        [property: JsonPropertyName("to")] IReadOnlyList<BrevoRecipient> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("textContent")] string TextContent);

    private sealed record BrevoSender(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("name")] string Name = "Glam by Mariya");

    private sealed record BrevoRecipient([property: JsonPropertyName("email")] string Email);
}
